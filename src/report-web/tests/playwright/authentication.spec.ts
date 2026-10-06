import { expect, test } from '@playwright/test'

test('config token timeout is classified before API and does not open a popup', async ({ page }) => {
  const requests: string[] = []
  await page.route('https://report-test.invalid/**', async (route) => {
    requests.push(route.request().url())
    await route.abort()
  })
  await page.goto('/tests/playwright/authentication-harness.html?mode=timeout')
  await expect(page.getByRole('alert')).toContainText('API を呼び出す前の認証がタイムアウト')
  await expect(page.getByRole('alert')).toContainText('MSAL timed_out')
  expect(await page.locator('.chat-panel.report-panel').evaluate((node) => getComputedStyle(node).display)).toBe('flex')
  expect((await page.getByRole('alert').boundingBox())!.height).toBeLessThan(200)
  await expect(page.getByRole('button', { name: '認証を続ける', exact: true })).toHaveCount(0)
  expect(await page.evaluate(() => (window as unknown as { authTestMetrics: { popup: number } }).authTestMetrics.popup)).toBe(0)
  expect(requests).toHaveLength(0)
  await page.getByRole('button', { name: '構成の取得を再試行' }).click()
  await expect(page.getByRole('alert')).toContainText('MSAL timed_out')
  expect(requests).toHaveLength(0)
})

test('interaction-required config failure only opens popup on the explicit user gesture', async ({ page }) => {
  let configCalls = 0
  await page.route('https://report-test.invalid/api/reports/config', async (route) => {
    configCalls++
    await route.fulfill({ json: {
      provider: 'graph', testYear: 2026, timeZone: 'Asia/Tokyo', promptHash: 'test',
      modelDeployment: 'test', modelVersion: 'test', sharePointConfigured: true,
    } })
  })
  await page.goto('/tests/playwright/authentication-harness.html?mode=interaction')
  await expect(page.getByRole('button', { name: '認証を続ける', exact: true })).toBeVisible()
  expect(await page.evaluate(() => (window as unknown as { authTestMetrics: { popup: number } }).authTestMetrics.popup)).toBe(0)
  expect(configCalls).toBe(0)
  await page.getByLabel('日報・週報の依頼').fill('保持する下書き')
  await page.getByRole('button', { name: '認証を続ける', exact: true }).click()
  await expect.poll(() => configCalls).toBe(1)
  await expect(page.getByRole('button', { name: '認証を続ける', exact: true })).toHaveCount(0)
  await expect(page.getByText('構成未取得：', { exact: false })).toHaveCount(0)
  await expect(page.getByLabel('日報・週報の依頼')).toHaveValue('保持する下書き')
  expect(await page.evaluate(() => (window as unknown as { authTestMetrics: object }).authTestMetrics)).toMatchObject({ popup: 1, userActivation: true })
})
