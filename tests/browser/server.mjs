import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';

const root = resolve('artifacts/browser-fixture');
mkdirSync(root, { recursive: true });
const run = mkdtempSync(join(root, 'run-'));
const fixture = spawnSync('dotnet', ['run', '--project', 'tests/AllenBuilding.BrowserFixture', '--configuration', 'Release', '--no-build', '--', run], { stdio: 'inherit' });
if (fixture.status !== 0) process.exit(fixture.status ?? 1);
writeFileSync(join(root, 'current.txt'), run, { mode: 0o600 });
const app = spawn('dotnet', ['run', '--project', 'src/AllenBuilding.Web', '--configuration', 'Release', '--no-build', '--no-launch-profile', '--urls', 'http://127.0.0.1:5088'], {
  stdio: 'inherit',
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Testing', Database__Provider: 'Sqlite', Billing__PlanningEnabled: 'true', Billing__RefundReviewEnabled: 'true', Billing__CashSourceReviewEnabled: 'true',
    ConnectionStrings__Office: `Data Source=${join(run, 'office.db')};Foreign Keys=True`, Protection__KeyDirectory: join(run, 'keys') },
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => app.kill(signal));
app.on('exit', code => process.exit(code ?? 0));
