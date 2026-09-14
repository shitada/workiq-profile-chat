import { expect, test } from '@playwright/test'

test('consent controls are visible and actionable', async ({ page }) => {
  await page.goto('/tests/playwright/consent-harness.html')

  const openLink = page.getByRole('link', { name: '認証ページを開く' })
  const sameTabLink = page.getByRole('link', { name: '同じタブで開く' })
  await expect(openLink).toBeVisible()
  await expect(sameTabLink).toBeVisible()
  await expect(openLink).toHaveAttribute(
    'href',
    /^https:\/\/logic-apis-eastus2\.consent\.azure-apim\.net\/login\?/,
  )
  await expect(openLink).toHaveAttribute('target', '_blank')
  await expect(sameTabLink).not.toHaveAttribute('target')

  await page.getByRole('button', { name: '認証URLをコピー' }).click()
  await expect(page.getByTestId('event')).toHaveText('copy')

  await page.getByRole('button', { name: '認証リンクを再発行' }).click()
  await expect(page.getByTestId('event')).toHaveText('refresh')

  await page.screenshot({
    path: 'test-results/consent-controls.png',
    fullPage: true,
  })
})
