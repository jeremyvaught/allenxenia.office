#!/usr/bin/env python3
"""Exercise a published Production DLL and independent MySQL restore using disposable local containers only."""
import argparse
import copy
from html.parser import HTMLParser
import http.client
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import shlex
import subprocess
import tempfile
import time
import urllib.parse
import uuid

spec = importlib.util.spec_from_file_location('release_package', Path(__file__).with_name('package-release.py'))
package = importlib.util.module_from_spec(spec); spec.loader.exec_module(package)
RUNTIME = 'mcr.microsoft.com/dotnet/aspnet:10.0'


def command(args, data=None, timeout=120):
    return subprocess.run(args, input=data, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=True, timeout=timeout).stdout


def mysql(container, sql, database=None):
    args = ['docker', 'exec', '-i', container, 'mysql', '--user=root', '--password=disposable-root', '--batch', '--skip-column-names']
    if database: args += ['--database=' + database]
    return command(args, sql.encode(), timeout=30).decode().strip()


class Inputs(HTMLParser):
    def __init__(self): super().__init__(); self.token = None
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'input' and attrs.get('name') == '__RequestVerificationToken': self.token = attrs.get('value')


def request(web, path, cookie=None, form=None):
    body = urllib.parse.urlencode(form) if form else ''
    raw = ('POST' if form else 'GET') + ' ' + path + ' HTTP/1.1\r\nHost: office.allenxenia.com\r\nX-Forwarded-For: 192.0.2.11\r\nX-Forwarded-Proto: https\r\nConnection: close\r\n'
    if cookie: raw += 'Cookie: ' + cookie + '\r\n'
    if form: raw += 'Content-Type: application/x-www-form-urlencoded\r\nContent-Length: ' + str(len(body.encode())) + '\r\n'
    raw += '\r\n' + body
    # A fixed loopback socket in the owned network namespace; no shell evaluates form values.
    script = 'exec 3<>/dev/tcp/127.0.0.1/5081; printf %s ' + shlex.quote(raw) + ' >&3; cat <&3'
    result = command(['docker', 'exec', web, '/bin/bash', '-c', script], timeout=15)
    if len(result) > 2 * 1024 * 1024: raise ValueError('Unexpected response size.')
    class Socket:
        def makefile(self, *_args, **_kwargs): return io.BytesIO(result)
    response = http.client.HTTPResponse(Socket()); response.begin(); payload = response.read().decode(errors='replace')
    return response.status, response.getheaders(), payload


def private_write(path, data):
    with path.open('x', encoding='utf-8') as stream: json.dump(data, stream)
    path.chmod(0o600)


def verify_release(release):
    release = Path(release).absolute(); manifest = package.verify(release)
    repository = Path(__file__).resolve().parent.parent
    artifacts = repository / 'artifacts' / 'release-verification'; artifacts.mkdir(parents=True, exist_ok=True, mode=0o700)
    if artifacts.is_symlink(): raise ValueError('Use a real local evidence directory.')
    owned = []; identifier = uuid.uuid4().hex; db = 'office-release-proof-' + identifier
    with tempfile.TemporaryDirectory(prefix='private-proof-', dir=artifacts) as temporary:
        state = Path(temporary); state.chmod(0o700)
        try:
            command(['docker', 'run', '--detach', '--rm', '--name', db, '--env', 'MYSQL_ROOT_PASSWORD=disposable-root', 'mysql:8.4']); owned.append(db)
            for _ in range(90):
                try:
                    # The entrypoint's temporary initialization server accepts socket connections too early.
                    command(['docker', 'exec', db, 'mysql', '--protocol=TCP', '--host=127.0.0.1', '--user=root', '--password=disposable-root', '--execute=SELECT 1'], timeout=5)
                    break
                except subprocess.SubprocessError: time.sleep(1)
            else: raise ValueError('Disposable MySQL did not start.')
            mysql(db, "CREATE DATABASE office_release_proof; CREATE DATABASE office_release_restored; CREATE USER 'office_runtime'@'%' IDENTIFIED BY 'disposable-runtime'; CREATE USER 'office_migrator'@'%' IDENTIFIED BY 'disposable-migrator'; GRANT SELECT,INSERT,UPDATE,DELETE ON office_release_proof.* TO 'office_runtime'@'%'; GRANT SELECT,INSERT,UPDATE,DELETE ON office_release_restored.* TO 'office_runtime'@'%'; GRANT ALL ON office_release_proof.* TO 'office_migrator'@'%';")
            for user, password in [('office_runtime', 'disposable-runtime'), ('office_migrator', 'disposable-migrator')]:
                command(['docker', 'exec', db, 'mysql', '--user=' + user, '--password=' + password, '--execute=SELECT 1'])
            command(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=Office disposable wrapping certificate', '-keyout', str(state / 'private.key'), '-out', str(state / 'certificate.pem'), '-days', '1'])
            (state / 'password').write_text('disposable-wrapping'); (state / 'password').chmod(0o600)
            command(['openssl', 'pkcs12', '-export', '-inkey', str(state / 'private.key'), '-in', str(state / 'certificate.pem'), '-out', str(state / 'wrapping.pfx'), '-passout', 'file:' + str(state / 'password')])
            for file in state.iterdir(): file.chmod(0o600)
            connection = 'Server=127.0.0.1;Database=office_release_proof;User ID=office_runtime;Password=disposable-runtime;SslMode=Disabled;MaximumPoolSize=10;'
            config = {'Database': {'Provider': 'MySql'}, 'AllowedHosts': 'office.allenxenia.com', 'ConnectionStrings': {'Office': connection}, 'Protection': {'KeyDirectory': '/private/keys', 'CertificatePath': '/private/wrapping.pfx', 'CertificatePassword': 'disposable-wrapping'}, 'Proxy': {'Enabled': True, 'TrustedAddresses': ['127.0.0.1']}, 'Notifications': {'Mode': 'Disabled', 'Enabled': False}, 'Billing': {'PlanningEnabled': False, 'CollectionReviewEnabled': False}, 'Telemetry': {'TraceSampleRatio': 0}}
            private_write(state / 'runtime.json', config)
            migrations = copy.deepcopy(config); migrations['ConnectionStrings'] = {'OfficeMigrator': connection.replace('office_runtime', 'office_migrator').replace('disposable-runtime', 'disposable-migrator')}
            private_write(state / 'migration.json', migrations)
            common = ['docker', 'run', '--rm', '--ulimit', 'core=0', '--network', 'container:' + db, '--user', str(os.getuid()) + ':' + str(os.getgid()), '--read-only', '--tmpfs', '/tmp:rw,noexec,nosuid,size=64m', '--mount', 'type=bind,source=' + str(release) + ',target=/app,readonly', '--mount', 'type=bind,source=' + str(state) + ',target=/private', '--workdir', '/app', '--env', 'ASPNETCORE_ENVIRONMENT=Production']
            migration_name = 'office-release-migrate-' + identifier; owned.append(migration_name)
            command(common + ['--name', migration_name, '--env', 'Office__RuntimeConfigFile=/private/migration.json', RUNTIME, 'dotnet', 'AllenBuilding.Web.dll', '--migrate'])
            recovery_name = 'office-release-recovery-refusal-' + identifier; owned.append(recovery_name)
            refused = subprocess.run(common + ['--name', recovery_name, '--env', 'Office__RuntimeConfigFile=/private/runtime.json', RUNTIME, 'dotnet', 'AllenBuilding.Web.dll', '--recover-manager-mfa'], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30)
            if refused.returncode != 1 or b'Manager recovery failed or was not authorized.' not in refused.stderr:
                raise ValueError('Unattended manager recovery was not refused.')
            mysql(db, "INSERT INTO buildings(id,name,time_zone_id) VALUES('11111111-1111-1111-1111-111111111111','Synthetic restore building','America/New_York'); INSERT INTO suites(id,building_id,name) VALUES('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111','Synthetic suite');", 'office_release_proof')

            def start(label, filename):
                name = 'office-release-' + label + '-' + identifier
                command(common + ['--detach', '--name', name, '--env', 'Office__RuntimeConfigFile=/private/' + filename, RUNTIME, 'dotnet', 'AllenBuilding.Web.dll', '--urls', 'http://127.0.0.1:5081']); owned.append(name)
                for _ in range(45):
                    try:
                        status, _headers, body = request(name, '/health/ready')
                        if status == 200 and json.loads(body)['status'] == 'Ready': return name
                    except (ValueError, KeyError, subprocess.SubprocessError, http.client.HTTPException): pass
                    time.sleep(1)
                raise ValueError('Published Production process did not become ready.')

            def stop(name): command(['docker', 'stop', '--timeout', '10', name], timeout=20)
            web = start('original', 'runtime.json')
            for path in ['/health/live', '/', '/css/app.css']:
                status, _headers, _body = request(web, path)
                if status != 200: raise ValueError('Published page or static asset unavailable.')
            status, _headers, _body = request(web, '/Portal')
            if status != 302: raise ValueError('Protected page did not require sign-in.')
            status, headers, body = request(web, '/Account/Login')
            inputs = Inputs(); inputs.feed(body)
            cookies = [value.split(';')[0] for name, value in headers if name.lower() == 'set-cookie' and value.startswith('.AspNetCore.Antiforgery.')]
            if status != 200 or len(cookies) != 1 or not inputs.token: raise ValueError('Protected form unavailable.')
            original_token = inputs.token; cookie = cookies[0]; stop(web)
            keys = list((state / 'keys').glob('key-*.xml'))
            if not keys or any('encryptedSecret' not in path.read_text() or '<masterKey' in path.read_text() for path in keys): raise ValueError('Persisted keys were not wrapped.')

            def counts(database):
                names = mysql(db, 'SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=' + "'" + database + "' ORDER BY TABLE_NAME").splitlines()
                if not names or any(not re.fullmatch(r'[A-Za-z0-9_]+', name) for name in names): raise ValueError('Unexpected database inventory.')
                return {name: int(mysql(db, 'SELECT COUNT(*) FROM `' + name + '`', database)) for name in names}
            original_counts = counts('office_release_proof')
            dump = command(['docker', 'exec', db, 'mysqldump', '--user=office_migrator', '--password=disposable-migrator', '--single-transaction', '--skip-lock-tables', '--no-tablespaces', '--set-gtid-purged=OFF', 'office_release_proof'])
            command(['docker', 'exec', '-i', db, 'mysql', '--user=root', '--password=disposable-root', '--database=office_release_restored'], data=dump)
            if original_counts != counts('office_release_restored'): raise ValueError('Restored table counts differ.')
            restored = copy.deepcopy(config); restored['ConnectionStrings']['Office'] = connection.replace('office_release_proof', 'office_release_restored'); restored['Protection']['KeyDirectory'] = '/private/restored-keys'; restored['Protection']['CertificatePath'] = '/private/restored-wrapping.pfx'
            shutil.copytree(state / 'keys', state / 'restored-keys'); (state / 'restored-keys').chmod(0o700)
            shutil.copyfile(state / 'wrapping.pfx', state / 'restored-wrapping.pfx'); (state / 'restored-wrapping.pfx').chmod(0o600)
            private_write(state / 'restored.json', restored)
            form = {'Email': 'unregistered@example.test', 'Password': 'Synthetic proof password', '__RequestVerificationToken': original_token}
            recovery = start('restored', 'restored.json')
            status, _headers, body = request(recovery, '/Account/Login', cookie=cookie, form=form)
            if status != 200 or 'Unable to sign in.' not in body: raise ValueError('Restored keys could not validate the original protected form.')
            stop(recovery)
            missing = copy.deepcopy(restored); missing['Protection']['KeyDirectory'] = '/private/missing-keys'; private_write(state / 'missing-keys.json', missing)
            empty = start('missingkeys', 'missing-keys.json')
            status, _headers, _body = request(empty, '/Account/Login', cookie=cookie, form=form)
            if status != 400: raise ValueError('Missing keys did not reject the original protected form.')
            stop(empty)
            if original_counts != counts('office_release_proof') or original_counts != counts('office_release_restored'): raise ValueError('Recovery probes changed application rows.')
            evidence = {'format': 1, 'application_revision': manifest['source_revision'], 'profile': 'Published Production DLL on isolated Linux .NET/MySQL; no Forge host configured', 'runtime': command(['docker', 'run', '--rm', RUNTIME, 'dotnet', '--list-runtimes']).decode().strip().splitlines(), 'tables': len(original_counts), 'counts': original_counts, 'public_static_live_ready': 'passed', 'private_sign_in_required': 'passed', 'unattended_manager_recovery': 'refused', 'separate_database_restore': 'matching table counts and ready', 'protected_form_with_restored_keys': 'passed', 'protected_form_without_keys': 'rejected', 'external_financial_writes': 'disabled', 'scope': 'Synthetic local containers and empty accounts; not off-host recovery, tenant pilot, Nginx/Supervisor or payment reconciliation proof'}
            filename = artifacts / ('published-' + manifest['source_revision'][:12] + '-' + identifier[:8] + '.json'); private_write(filename, evidence)
            return filename
        finally:
            for name in reversed(owned):
                subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)


def main():
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('release'); args = parser.parse_args()
    try: print(json.dumps({'evidence': str(verify_release(args.release)), 'result': 'passed'}))
    except (OSError, ValueError, subprocess.SubprocessError, http.client.HTTPException):
        parser.exit(1, 'Published release proof failed; no external host or database was used. Check Docker/runtime, public package, private fixture permissions and recovery behavior.\n')


if __name__ == '__main__': main()
