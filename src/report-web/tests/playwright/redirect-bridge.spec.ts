import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import { resolve } from 'node:path'

const origin = 'https://report-bridge.test'
const config = JSON.parse(await readFile(resolve('staticwebapp.config.json'), 'utf8'))
const globalHeaders = config.globalHeaders as Record<string, string>
const bridgeHeaders = { ...globalHeaders, ...config.routes.find((route: { route: string }) => route.route === '/redirect.html').headers }

async function installPages(page: Page, parentHeaders: Record<string, string>, redirectHeaders: Record<string, string>) {
  await page.context().route(`${origin}/**`, async (route) => {
    const path = new URL(route.request().url()).pathname
    if (path === '/redirect.html') {
      await route.fulfill({ headers: { ...redirectHeaders, 'Content-Type': 'text/html' }, body: await readFile(resolve('dist/redirect.html')) })
    } else if (path.startsWith('/assets/') && /^\/assets\/[\w.-]+\.js$/.test(path)) {
      await route.fulfill({ contentType: 'text/javascript', body: await readFile(resolve(`dist${path}`)) })
    } else {
      await route.fulfill({ headers: { ...parentHeaders, 'Content-Type': 'text/html' }, body: '<!doctype html><title>Bridge test</title><body>Parent</body>' })
    }
  })
  await page.goto(origin)
}

async function bridgeResponse(page: Page, mode: 'silent' | 'popup' = 'silent') {
  return page.evaluate(async ({ origin, mode }) => {
    const id = crypto.randomUUID()
    const state = btoa(JSON.stringify({ id, meta: { interactionType: mode } }))
    const url = `${origin}/redirect.html#code=synthetic-test-only&state=${encodeURIComponent(state)}`
    return new Promise<string | null>((resolve) => {
      const channel = new BroadcastChannel(id)
      const timer = setTimeout(() => { channel.close(); resolve(null) }, 1500)
      channel.onmessage = (event) => {
        clearTimeout(timer)
        channel.close()
        resolve(event.data.payload)
      }
      if (mode === 'popup') window.open(url, '_blank')
      else {
        const iframe = document.createElement('iframe')
        iframe.src = url
        document.body.append(iframe)
      }
    })
  }, { origin, mode })
}

test('reproduces original parent frame-src block before MSAL bridge can respond', async ({ page }) => {
  const consoleErrors: string[] = []
  page.on('console', (message) => { if (message.type() === 'error') consoleErrors.push(message.text()) })
  const previousHeaders = { ...globalHeaders, 'Content-Security-Policy': globalHeaders['Content-Security-Policy'].replace("frame-src 'self'", 'frame-src') }
  await installPages(page, previousHeaders, bridgeHeaders)
  expect(await bridgeResponse(page)).toBeNull()
  expect(consoleErrors.some((error) => error.includes('frame-src'))).toBe(true)
})

test('reproduces original bridge frame-ancestors/XFO denial even when parent permits self', async ({ page }) => {
  const consoleErrors: string[] = []
  page.on('console', (message) => { if (message.type() === 'error') consoleErrors.push(message.text()) })
  await installPages(page, globalHeaders, { ...bridgeHeaders, 'Content-Security-Policy': globalHeaders['Content-Security-Policy'], 'X-Frame-Options': 'DENY' })
  expect(await bridgeResponse(page)).toBeNull()
  expect(consoleErrors.some((error) => error.includes('frame-ancestors') || error.includes('X-Frame-Options'))).toBe(true)
})

test('real bundled MSAL v5 bridge responds inside same-origin silent iframe with scoped headers', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', (error) => errors.push(error.message))
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()) })
  await installPages(page, globalHeaders, bridgeHeaders)
  expect(await bridgeResponse(page)).toContain('code=synthetic-test-only')
  expect(errors).toEqual([])
  expect(globalHeaders['Content-Security-Policy']).toContain("frame-ancestors 'none'")
  expect(globalHeaders['X-Frame-Options']).toBe('DENY')
  expect(bridgeHeaders['X-Frame-Options']).toBe('SAMEORIGIN')
  expect(bridgeHeaders['Content-Security-Policy']).toContain("frame-ancestors 'self'")
  expect(bridgeHeaders['Cross-Origin-Opener-Policy']).toBe('')
})

test('real bundled bridge retains popup response behavior without credentials', async ({ page }) => {
  await installPages(page, globalHeaders, bridgeHeaders)
  expect(await bridgeResponse(page, 'popup')).toContain('code=synthetic-test-only')
})

test('bridge remains blocked in a cross-origin frame', async ({ page }) => {
  const errors: string[] = []
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()) })
  await installPages(page, globalHeaders, bridgeHeaders)
  await page.route('https://untrusted-parent.test/', (route) => route.fulfill({
    contentType: 'text/html', body: '<!doctype html><title>Other origin</title><body>External parent</body>',
  }))
  await page.goto('https://untrusted-parent.test/')
  expect(await bridgeResponse(page)).toBeNull()
  expect(errors.some((error) => error.includes('frame-ancestors') || error.includes('X-Frame-Options'))).toBe(true)
})
