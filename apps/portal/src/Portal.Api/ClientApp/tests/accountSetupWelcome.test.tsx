import React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import AccountSetupWelcome from '../src/AccountSetupWelcome'

describe('AccountSetupWelcome', () => {
  it('renders the requested welcome state for an account awaiting access', () => {
    const markup = renderToStaticMarkup(
      <AccountSetupWelcome
        accountStatus="pendingSetup"
        displayName="Jordan Greer"
        onRetry={vi.fn()}
      />,
    )

    expect(markup).toContain('Welcome To Arda')
    expect(markup).toContain('Please contact your system administrator to finish setting up your account.')
    expect(markup).toContain('Signed in as <strong>Jordan Greer</strong>')
    expect(markup).toContain('/brand/arda-mark.png')
    expect(markup).toContain('/brand/arda-mark-reversed.png')
    expect(markup).not.toContain('account-setup-orbit')
    expect(markup).not.toContain('account-setup-mark-surface')
    expect(markup).not.toContain('account-setup-visual-label')
    expect(markup).not.toContain('Try again')
  })

  it('keeps an unavailable access store distinct from first-time setup', () => {
    const markup = renderToStaticMarkup(
      <AccountSetupWelcome
        accountStatus="unavailable"
        displayName="Jordan Greer"
        onRetry={vi.fn()}
      />,
    )

    expect(markup).toContain('We could not confirm your access')
    expect(markup).toContain('Try again')
    expect(markup).not.toContain('Welcome To Arda')
  })
})
