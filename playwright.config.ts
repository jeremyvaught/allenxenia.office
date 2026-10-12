import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests/browser',
  outputDir: './artifacts/browser',
  reporter: 'list',
  use: { baseURL: 'http://127.0.0.1:5088' },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', use: { ...devices['iPhone 13'], defaultBrowserType: 'chromium' } },
  ],
  webServer: {
    command: 'dotnet run --project src/AllenBuilding.Web --configuration Release --no-build --no-launch-profile --urls http://127.0.0.1:5088',
    env: { ASPNETCORE_ENVIRONMENT: 'Development' },
    url: 'http://127.0.0.1:5088/health/live',
    reuseExistingServer: false,
    timeout: 30000,
  },
});
