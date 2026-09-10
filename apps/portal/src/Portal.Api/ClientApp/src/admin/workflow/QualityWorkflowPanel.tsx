import { useEffect, useRef, useState } from 'react'
import { AlertCircle, Check, Clock3, FlaskConical, GitBranch, Redo2, Save, Undo2, Upload, X } from 'lucide-react'
import { qualityAdminApi } from '../qualityApi'
import type { QualityAssignmentOptions } from '../types'
import { WorkflowCanvas } from './WorkflowCanvas'
import { WorkflowInspector } from './WorkflowInspector'
import { WorkflowTestPanel } from './WorkflowTestPanel'
import { removeNode, validateGraph } from './model'
import type { WorkflowDocument, WorkflowGraph, WorkflowSimulation } from './types'
import { useWorkflowLeaveGuard } from './useWorkflowLeaveGuard'
import './workflow-editor.css'

export default function QualityWorkflowPanel() {
  const [document, setDocument] = useState<WorkflowDocument | null>(null)
  const [options, setOptions] = useState<QualityAssignmentOptions | null>(null)
  const [graph, setGraph] = useState<WorkflowGraph | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [past, setPast] = useState<WorkflowGraph[]>([])
  const [future, setFuture] = useState<WorkflowGraph[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [testOpen, setTestOpen] = useState(false)
  const [simulation, setSimulation] = useState<WorkflowSimulation | null>(null)
  const [reviewOpen, setReviewOpen] = useState(false)
  const reviewRef = useRef<HTMLDialogElement>(null)
  const leaveRef = useRef<HTMLDialogElement>(null)
  const dirty = !!graph && !!document && JSON.stringify(graph) !== JSON.stringify(document.draft)
  const leaveGuard = useWorkflowLeaveGuard(dirty)
  const structuralIssues = graph ? validateGraph(graph) : []
  const issues = [...structuralIssues, ...(!dirty ? document?.validation.issues.filter(issue => !structuralIssues.some(local => local.nodeId === issue.nodeId)) ?? [] : [])]
  const publishedMatches = !!document?.published && JSON.stringify(document.published) === JSON.stringify(graph)

  async function load() {
    setBusy(true); setError(null)
    try {
      const [next, directory] = await Promise.all([
        qualityAdminApi<WorkflowDocument>('/api/admin/workflow'),
        qualityAdminApi<QualityAssignmentOptions>('/api/admin/workflow/options'),
      ])
      setDocument(next); setGraph(next.draft); setOptions(directory)
      setPast([]); setFuture([]); setSimulation(null)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Workflow settings are unavailable.') }
    finally { setBusy(false) }
  }
  useEffect(() => { void load() }, [])
  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => { if (leaveGuard.approved.current) return; event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty, leaveGuard.approved])
  useEffect(() => {
    if (reviewOpen) reviewRef.current?.showModal()
    else reviewRef.current?.close()
  }, [reviewOpen])
  useEffect(() => {
    if (leaveGuard.destination) { leaveRef.current?.showModal(); leaveRef.current?.querySelector<HTMLButtonElement>('.solid-button')?.focus() }
    else leaveRef.current?.close()
  }, [leaveGuard.destination])

  function change(next: WorkflowGraph) {
    if (!graph || busy || JSON.stringify(next) === JSON.stringify(graph)) return
    setPast(current => [...current.slice(-49), graph]); setFuture([])
    setGraph(next); setSimulation(null); setMessage(null)
  }
  function undo() {
    const previous = past.at(-1)
    if (!previous || !graph) return
    setFuture(current => [graph, ...current]); setPast(past.slice(0, -1)); setGraph(previous); setSimulation(null)
  }
  function redo() {
    const next = future[0]
    if (!next || !graph) return
    setPast(current => [...current, graph]); setFuture(future.slice(1)); setGraph(next); setSimulation(null)
  }
  async function save() {
    if (!graph || !document) return
    setBusy(true); setError(null); setMessage(null)
    try {
      const saved = await qualityAdminApi<WorkflowDocument>('/api/admin/workflow/draft', { method: 'PUT', body: JSON.stringify({ version: document.version, graph }) })
      setDocument(saved); setGraph(saved.draft); setMessage('Draft saved. Live routing is unchanged.')
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Could not save the draft.') }
    finally { setBusy(false) }
  }
  async function publish() {
    if (!document || dirty || issues.length) return
    setBusy(true); setError(null); setMessage(null)
    try {
      const published = await qualityAdminApi<WorkflowDocument>('/api/admin/workflow/publish', { method: 'POST', body: JSON.stringify({ version: document.version }) })
      setDocument(published); setGraph(published.draft); setReviewOpen(false)
      setMessage(`Version ${published.publishedRevision} is live. New QA actions now use this workflow.`)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Could not publish this workflow.'); setReviewOpen(false) }
    finally { setBusy(false) }
  }
  if (!graph || !options || !document) return <section className="admin-surface workflow-loading" aria-live="polite">
    <GitBranch size={28} /><h2>Quality workflow</h2>{error ? <><p role="alert">{error}</p><button type="button" className="ghost-button" onClick={() => void load()} disabled={busy}>Try again</button></> : <p>Loading your actions and queue routing…</p>}
  </section>
  const selected = graph.nodes.find(node => node.id === selectedId)
  return <section className="workflow-editor" aria-label="Quality workflow editor">
    <header className="workflow-toolbar">
      <div className="workflow-document-name"><span className="workflow-document-icon"><GitBranch size={19} /></span><div><label className="sr-only" htmlFor="workflow-name">Workflow name</label><input id="workflow-name" value={graph.name} maxLength={160} disabled={busy} onChange={event => change({ ...graph, name: event.target.value })} /><span>{document.publishedRevision ? `Live version ${document.publishedRevision}` : 'Current assignment rules are live'} · {dirty ? 'Unsaved changes' : publishedMatches ? 'Published' : 'Draft'}</span></div></div>
      <div className="workflow-toolbar-actions"><div className="workflow-history-buttons"><button type="button" className="workflow-icon-button" aria-label="Undo change" title="Undo" disabled={busy || !past.length} onClick={undo}><Undo2 size={16} /></button><button type="button" className="workflow-icon-button" aria-label="Redo change" title="Redo" disabled={busy || !future.length} onClick={redo}><Redo2 size={16} /></button></div>
        <button className="ghost-button" type="button" aria-pressed={testOpen} onClick={() => setTestOpen(!testOpen)}><FlaskConical size={15} /> Test path</button>
        <button className="ghost-button" type="button" disabled={busy || (!dirty && document.version > 0)} onClick={() => void save()}><Save size={15} /> Save draft</button>
        <button className="solid-button" type="button" disabled={busy || dirty || document.version === 0 || !!issues.length || publishedMatches} title={dirty || document.version === 0 ? 'Save your draft before publishing' : issues.length ? 'Resolve the highlighted issues before publishing' : 'Review and publish routing changes'} onClick={() => setReviewOpen(true)}><Upload size={15} /> Publish</button>
      </div>
    </header>
    <div className="workflow-context-bar"><span><span className="workflow-scope-dot" /> Quality Assurance</span><p>Decide what happens next, and who gets the work.</p><span className={`workflow-validation ${issues.length ? 'has-issues' : ''}`}>{issues.length ? <AlertCircle size={14} /> : <Check size={14} />}{issues.length ? `${issues.length} items to resolve` : 'Ready to test'}</span></div>
    {error && <p className="workflow-banner workflow-error" role="alert"><AlertCircle size={16} />{error}<button type="button" className="workflow-icon-button" aria-label="Dismiss error" onClick={() => setError(null)}><X size={15} /></button></p>}
    {message && <p className="workflow-banner" role="status"><Check size={16} />{message}</p>}
    <div className="workflow-workspace"><WorkflowCanvas graph={graph} options={options} selectedId={selectedId} onSelect={setSelectedId} onChange={change} trace={simulation?.path} disabled={busy} />
      <WorkflowInspector graph={graph} node={selected} options={options} issues={issues.filter(issue => issue.nodeId === selectedId)} onChange={change} onClose={() => setSelectedId(null)} onDelete={() => { if (selectedId) change(removeNode(graph, selectedId)); setSelectedId(null) }} disabled={busy} />
    </div>
    {testOpen && <WorkflowTestPanel graph={graph} options={options} result={simulation} onResult={setSimulation} onClose={() => { setTestOpen(false); setSimulation(null) }} onSelect={setSelectedId} />}
    <footer className="workflow-footer"><span>{graph.nodes.filter(node => node.type === 'trigger').length} actions · {graph.nodes.length} blocks · {graph.edges.length} connections</span><span><Clock3 size={13} />{document.updatedAt ? `Saved ${new Date(document.updatedAt).toLocaleString()}` : 'Save a draft to keep your changes'}</span></footer>
    {!!issues.length && <details className="workflow-validation-list"><summary><AlertCircle size={14} /> Review {issues.length} items before publishing</summary><ul>{issues.map((issue, index) => <li key={index}>{issue.nodeId ? <button type="button" onClick={() => setSelectedId(issue.nodeId!)}>{issue.message}</button> : issue.message}</li>)}</ul></details>}
    {!!document.history.length && <details className="workflow-activity"><summary><Clock3 size={14} /> Version activity</summary>{document.published && !publishedMatches && <button type="button" className="ghost-button" disabled={busy} onClick={() => change(document.published!)}>Restore live version to draft</button>}<ol>{document.history.map(entry => <li key={entry.id}><span>{entry.action === 'DraftSaved' ? 'Draft saved' : entry.action} {entry.revision > 0 && `· v${entry.revision}`}</span><span>{entry.displayName || entry.accountName} · {new Date(entry.occurredAt).toLocaleString()}</span></li>)}</ol></details>}
    <dialog className="workflow-publish-dialog" ref={reviewRef} aria-labelledby="workflow-publish-title" onCancel={event => { if (busy) event.preventDefault(); else setReviewOpen(false) }}>
      <span className="workflow-document-icon"><Upload size={22} /></span><h2 id="workflow-publish-title">Publish this workflow?</h2><p>Version {document.publishedRevision + 1} will apply to future QA actions immediately. Existing shipments move only when a configured action runs.</p>
      <div className="workflow-publish-summary"><strong>{graph.name}</strong><span>{graph.nodes.filter(node => node.type === 'trigger').length} configured actions · {graph.nodes.filter(node => node.type === 'route').length} destinations</span><span>{simulation?.isValid ? 'A sample path has been tested.' : 'You can test a sample path before publishing.'}</span></div>
      {!document.published && <p>This replaces the current automatic assignment rules.</p>}
      <footer><button type="button" className="ghost-button" disabled={busy} onClick={() => setReviewOpen(false)}>Keep editing</button><button type="button" className="solid-button" disabled={busy} onClick={() => void publish()}><Upload size={15} />{busy ? 'Publishing…' : `Publish version ${document.publishedRevision + 1}`}</button></footer>
    </dialog>
    <dialog className="workflow-publish-dialog" ref={leaveRef} aria-labelledby="workflow-leave-title" onCancel={leaveGuard.stay}><h2 id="workflow-leave-title">Keep your draft changes?</h2><p>You have unsaved edits. Stay here to save your draft, or discard these edits and continue.</p><footer><button type="button" className="ghost-button" onClick={leaveGuard.leave}>Discard and leave</button><button type="button" className="solid-button" autoFocus onClick={leaveGuard.stay}>Keep editing</button></footer></dialog>
  </section>
}
