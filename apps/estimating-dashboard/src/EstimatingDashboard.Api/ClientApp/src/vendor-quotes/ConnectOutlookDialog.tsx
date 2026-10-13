import { Check, Download, LoaderCircle, Mail, Monitor, RefreshCw, ShieldCheck } from 'lucide-react'
import Modal from './Modal'
import { dateTime, syncState } from './model'
import { outlookConnectionState } from './outlookSyncModel'
import type { VendorSync } from './types'
import './outlook-connection.css'

export default function ConnectOutlookDialog({ sync, syncError, syncLoaded, checking, checkedAt, onRefresh, onClose, canConnect }: {
  sync: VendorSync[]; syncError: string | null; syncLoaded: boolean; checking: boolean; checkedAt: number
  onRefresh: () => void; onClose: () => void; canConnect: boolean
}) {
  const health = outlookConnectionState(sync, syncLoaded, syncError, new Date(checkedAt))
  return <Modal title="Connect your Outlook" subtitle="Bring quote conversations into the same place as your team's updates." onClose={onClose}>
    <div className="vq-connect-intro"><div className="vq-connect-icon"><Mail size={27} /></div><div>
      <strong>Classic Outlook for Windows</strong><p>Use your existing Outlook profile. Messages stay in Outlook and a copy is added to the matching quote in Arda.</p>
    </div></div>
    <ol className="vq-setup-steps">
      <li><span>1</span><div><strong>Download and extract the connector</strong><p>Keep the extracted folder on your work computer and open Classic Outlook.</p></div></li>
      <li><span>2</span><div><strong>Run Install.cmd once</strong><p>Confirm your company's HTTPS Arda address and choose your work mailbox. It starts automatically when you sign in to Windows and runs in the background.</p></div></li>
      <li><span>3</span><div><strong>Send from Outlook as usual</strong><p><code>Quote 4445 - RM004420</code> links the sent material request and vendor replies to its RFQ. A sent request becomes Waiting on vendor; confirm Quote received after reviewing the reply.</p></div></li>
    </ol>
    <div className="vq-info-line"><ShieldCheck size={17} /><p>Only messages matching a quote you can access are imported. Internal notes are shared in Arda and are never sent by email.</p></div>
    <div className="vq-info-line"><Monitor size={17} /><p>Keep Classic Outlook signed in and stay connected to your company network or VPN. Use <b>Status.cmd</b> to check setup, <b>Stop.cmd</b> to pause, and <b>Start-Background.cmd</b> to resume.</p></div>
    <section className="vq-connection-status" aria-label="Outlook connection status">
      <div className="vq-connection-heading"><span className={`vq-status vq-status-${health.tone}`}><i />{health.label}</span><button className="qs-text-button" type="button" disabled={checking} onClick={onRefresh}>{checking ? <LoaderCircle size={13} className="vq-spinner" /> : <RefreshCw size={13} />}Check again</button></div>
      {syncError && <p className="vq-error" role="alert">{syncError}</p>}
      {syncLoaded && !syncError && sync.length === 0 && <p className="vq-connection-help">Waiting for your first connection. Finish setup on your work computer; this status updates automatically.</p>}
      {sync.map(item => <div className="vq-mailbox" key={JSON.stringify([item.mailbox, item.clientName])}><strong>{item.mailbox}</strong><span className={`vq-status vq-status-${syncState([item], new Date(checkedAt)).tone}`}><i />{syncState([item], new Date(checkedAt)).label}</span><small>Last checked {dateTime(item.lastCheckedAt)}</small><small>Last successful check {dateTime(item.lastSuccessAt)}</small>
        {item.error ? <p className="vq-error">{item.error}</p> : <span><Check size={14} /> {item.importedCount} imported at last check</span>}
        {item.deferredCount > 0 && <p>{item.deferredCount} messages need another check. The connector will retry them.</p>}
        {!item.error && syncState([item], new Date(checkedAt)).tone !== 'success' && <p>Open Classic Outlook, check your company connection, then run <b>Start-Background.cmd</b> if syncing was paused.</p>}
      </div>)}
      <p className="vq-connection-help">Status refreshes automatically. If an edited email cannot be verified automatically, mark its sent email <b>Rates requested</b> in Arda. If the connector is unavailable, upload the sent email or reply to its RFQ.</p>
    </section>
    <footer className="vq-modal-footer"><button className="vq-button" onClick={onClose} type="button">Done</button>
      {canConnect && <a className="vq-button vq-button-primary" href="/api/vendor-quotes/connector" download><Download size={16} /> Download connector</a>}
    </footer>
  </Modal>
}
