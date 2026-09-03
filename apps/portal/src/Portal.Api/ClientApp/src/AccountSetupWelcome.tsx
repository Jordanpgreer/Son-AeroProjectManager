import {
  AlertTriangle,
  Clock,
  RefreshCw,
  ShieldCheck,
} from 'lucide-react'
import './account-setup-welcome.css'

export type AccountStatus = 'pendingSetup' | 'inactive' | 'unavailable'

interface AccountSetupWelcomeProps {
  accountStatus: AccountStatus
  displayName: string
  onRetry: () => void
}

const accountStatusContent: Record<AccountStatus, {
  kicker: string
  badge: string
  heading: string
  detail: string
  reassurance: string
}> = {
  pendingSetup: {
    kicker: 'Account setup',
    badge: 'Setup required',
    heading: 'Welcome To Arda',
    detail: 'Please contact your system administrator to finish setting up your account.',
    reassurance: 'Arda will be ready for you after your application access has been assigned.',
  },
  inactive: {
    kicker: 'Account status',
    badge: 'Access inactive',
    heading: 'Your Arda access is inactive',
    detail: 'Please contact your system administrator to restore access to your account.',
    reassurance: 'Your applications and permissions remain protected while access is inactive.',
  },
  unavailable: {
    kicker: 'Connection status',
    badge: 'Status unavailable',
    heading: 'We could not confirm your access',
    detail: 'Arda could not verify your account status. Please try again in a moment.',
    reassurance: 'No account settings were changed.',
  },
}

function StatusIcon({ status }: { status: AccountStatus }) {
  if (status === 'unavailable') {
    return <AlertTriangle size={16} strokeWidth={1.9} aria-hidden="true" />
  }

  if (status === 'inactive') {
    return <ShieldCheck size={16} strokeWidth={1.9} aria-hidden="true" />
  }

  return <Clock size={16} strokeWidth={1.9} aria-hidden="true" />
}

export default function AccountSetupWelcome({
  accountStatus,
  displayName,
  onRetry,
}: AccountSetupWelcomeProps) {
  const content = accountStatusContent[accountStatus]
  const friendlyName = displayName.trim()
  const unavailable = accountStatus === 'unavailable'

  return (
    <main className="portal-main account-setup-main">
      <section
        className={`account-setup-gateway account-setup-gateway--${accountStatus}`}
        data-account-status={accountStatus}
        aria-labelledby="account-setup-title"
        aria-describedby="account-setup-detail account-setup-reassurance"
        aria-live={unavailable ? 'assertive' : 'polite'}
        role={unavailable ? 'alert' : undefined}
      >
        <div className="account-setup-visual" aria-hidden="true">
          <img
            className="account-setup-mark account-setup-mark--standard"
            src="/brand/arda-mark.png"
            alt=""
            width="1254"
            height="1254"
          />
          <img
            className="account-setup-mark account-setup-mark--reversed"
            src="/brand/arda-mark-reversed.png"
            alt=""
            width="1254"
            height="1254"
          />
        </div>

        <div className="account-setup-copy">
          <div className="account-setup-meta">
            <span className="kicker">{content.kicker}</span>
            <span className="account-setup-badge">
              <StatusIcon status={accountStatus} />
              {content.badge}
            </span>
          </div>

          {friendlyName && (
            <p className="account-setup-person">
              Signed in as <strong>{friendlyName}</strong>
            </p>
          )}

          <h1 id="account-setup-title">{content.heading}</h1>
          <p className="account-setup-detail" id="account-setup-detail">{content.detail}</p>

          <div className="account-setup-reassurance" id="account-setup-reassurance">
            <ShieldCheck size={18} strokeWidth={1.8} aria-hidden="true" />
            <span>{content.reassurance}</span>
          </div>

          {unavailable && (
            <button className="account-setup-retry" type="button" onClick={onRetry}>
              <RefreshCw size={17} strokeWidth={1.9} aria-hidden="true" />
              Try again
            </button>
          )}
        </div>
      </section>
    </main>
  )
}
