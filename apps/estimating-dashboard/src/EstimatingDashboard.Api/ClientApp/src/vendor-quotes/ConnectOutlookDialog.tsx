import { Check, Download, Mail, Monitor, ShieldCheck } from 'lucide-react'
import Modal from './Modal'
import { dateTime, syncState } from './model'
import type { VendorSync } from './types'

export default function ConnectOutlookDialog({ sync, syncError, onClose, canConnect }: {
  sync: VendorSync[]; syncError: string | null; onClose: () => void; canConnect: boolean
}) {
  const health = syncState(sync)
  return <Modal title="Connect your Outlook" subtitle="Bring quote conversations into the same place as your team's updates." onClose={onClose}>
    <div className="vq-connect-intro"><div className="vq-connect-icon"><Mail size={27} /></div><div>
      <strong>Classic Outlook for Windows</strong><p>Use your existing Outlook profile. Messages stay in Outlook and a copy is added to the matching quote in Arda.</p>
    </div></div>
    <ol className="vq-setup-steps">
      <li><span>1</span><div><strong>Download and extract the connector</strong><p>Keep the folder somewhere on your work computer.</p></div></li>
      <li><span>2</span><div><strong>Open Outlook, then start the connector</strong><p>Double-click <b>Start.cmd</b> in the extracted folder. Keep it running while you work.</p></div></li>
      <li><span>3</span><div><strong>Keep using your usual subject line</strong><p><code>Quote 4445</code> and its replies are matched to quote 4445. Incoming and sent messages appear together.</p></div></li>
    </ol>
    <div className="vq-info-line"><ShieldCheck size={17} /><p>Only messages matching a quote you can access are imported. Internal notes are shared in Arda and are never sent by email.</p></div>
    <div className="vq-info-line"><Monitor size={17} /><p>Sync pauses when your computer, Outlook, or the connector is closed and catches up when it runs again. Company Outlook settings may require approval.</p></div>
    <section className="vq-connection-status" aria-label="Outlook connection status">
      <span className={`vq-status vq-status-${health.tone}`}><i />{health.label}</span>
      {syncError && <p className="vq-error" role="alert">{syncError}</p>}
      {sync.map(item => <div className="vq-mailbox" key={item.mailbox}><strong>{item.mailbox}</strong><small>Last checked {dateTime(item.lastCheckedAt)}</small>
        {item.error ? <p className="vq-error">{item.error}</p> : <span><Check size={14} /> {item.importedCount} imported at last check</span>}
        {item.deferredCount > 0 && <p>{item.deferredCount} messages need another check. The connector will retry them.</p>}
      </div>)}
    </section>
    <footer className="vq-modal-footer"><button className="vq-button" onClick={onClose} type="button">Done</button>
      {canConnect && <a className="vq-button vq-button-primary" href="/api/vendor-quotes/connector" download><Download size={16} /> Download connector</a>}
    </footer>
  </Modal>
}
