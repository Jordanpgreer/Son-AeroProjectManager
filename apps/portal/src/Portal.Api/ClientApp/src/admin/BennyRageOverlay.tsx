import { useEffect } from 'react'
import { createPortal } from 'react-dom'
import './benny-rage.css'

/**
 * Admin-only easter egg. Disabling the keyword assistant provokes Benny: he swells
 * to fill the screen, turns blood red, and the console glitches while he shouts.
 * Purely cosmetic - the setting itself changes exactly as it always did.
 */

// Traced from the shipped assistant art (prototypes/bloub-states) so the silhouette
// stays recognisable at full-screen size, where the source GIF would pixelate.
const BENNY_BODY = 'M250.9 149C254.1 154.2 256.8 160.4 257.7 166.3C258.7 172.2 258.1 178.8 256.7 184.6C255.3 190.5 252.8 196.5 249.5 201.5C246.2 206.6 241.7 211.3 236.9 214.9C232.1 218.5 225.9 220.7 220.6 223C215.2 225.2 209.6 225.9 204.9 228.2C200.2 230.5 196.9 234.4 192.5 236.8C188.1 239.3 183.4 241.8 178.5 243.1C173.6 244.4 168.3 244.8 163.2 244.8C158.2 244.8 153 244.3 148.1 243.1C143.2 241.9 138.3 240.1 134 237.7C129.7 235.3 127 230.5 122.2 228.7C117.4 227 110.7 228.6 105.1 227.3C99.4 226 93.3 224 88.2 221C83.1 218 78.3 213.8 74.6 209.1C70.9 204.5 68 198.9 66 193.3C63.9 187.6 62.4 181.4 62.3 175.5C62.2 169.6 63.6 163.2 65.4 157.6C67.2 152 69.9 146.4 73.2 141.7C76.4 136.9 82.5 133.8 85.2 129.2C87.9 124.6 87.4 119 89.1 113.9C90.9 108.7 92.6 103 95.6 98.3C98.6 93.6 102.6 89 107 85.6C111.4 82.2 116.8 79.5 122.1 77.7C127.4 76 133.3 75 138.9 75.2C144.4 75.5 150.3 77.4 155.4 79.4C160.6 81.5 164.7 86.6 169.6 87.6C174.5 88.6 179.5 85.7 184.8 85.5C190.1 85.3 196.1 84.9 201.3 86.2C206.6 87.5 211.7 90.2 216.1 93.3C220.5 96.4 224.6 100.5 227.7 104.9C230.9 109.3 233.1 114.6 234.8 119.7C236.6 124.7 235.8 130.4 238.5 135.2C241.1 140.1 247.7 143.8 250.9 149Z'
const BENNY_MOUTH = 'M126 205L138 216L150 205L162 216L174 205L186 216L196 205L188 227L134 227Z'

export const BENNY_RAGE_MS = 4200
export const BENNY_RAGE_REDUCED_MS = 2400

export default function BennyRageOverlay({
  assistantName,
  onFinished,
}: {
  assistantName: string
  onFinished: () => void
}) {
  const reducedMotion = typeof window !== 'undefined'
    && window.matchMedia('(prefers-reduced-motion: reduce)').matches

  useEffect(() => {
    // The overlay is portalled to <body>, so shaking #root never moves it.
    const appRoot = document.getElementById('root')
    if (!reducedMotion) appRoot?.classList.add('benny-rage-screen')
    const timer = window.setTimeout(onFinished, reducedMotion ? BENNY_RAGE_REDUCED_MS : BENNY_RAGE_MS)
    const skipOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onFinished()
    }
    window.addEventListener('keydown', skipOnEscape)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('keydown', skipOnEscape)
      appRoot?.classList.remove('benny-rage-screen')
    }
  }, [onFinished, reducedMotion])

  return createPortal(
    <div
      className={`benny-rage-stage${reducedMotion ? ' is-still' : ''}`}
      aria-hidden="true"
      onClick={onFinished}
    >
      <div className="benny-rage-scrim" />
      <div className="benny-rage-tremble">
        <svg className="benny-rage-figure" viewBox="0 0 320 320" role="presentation" focusable="false">
          <defs>
            <radialGradient id="benny-rage-skin" gradientUnits="userSpaceOnUse" cx="132" cy="122" r="196">
              <stop offset="0%" stopColor="#ff5330" />
              <stop offset="52%" stopColor="#cf1212" />
              <stop offset="100%" stopColor="#4d0101" />
            </radialGradient>
            <clipPath id="benny-rage-silhouette">
              <path d={BENNY_BODY} />
            </clipPath>
          </defs>
          <g clipPath="url(#benny-rage-silhouette)">
            <path d={BENNY_BODY} fill="url(#benny-rage-skin)" />
            {/* Benny's everyday flat red, dissolving as the rage takes over. */}
            <path className="benny-rage-calm-skin" d={BENNY_BODY} fill="#e8483f" />
            <g className="benny-rage-eyes">
              <rect x="100.6" y="111.1" width="35.2" height="83.7" rx="17.6" transform="rotate(13.7 118.2 152.9)" />
              <rect x="159.9" y="121.7" width="33.1" height="83.5" rx="16.5" transform="rotate(16.1 176.5 163.4)" />
            </g>
            {/* Brows share the body gradient in user space, so they read as one surface. */}
            <g className="benny-rage-brows" fill="url(#benny-rage-skin)">
              <rect x="60" y="80" width="110" height="60" transform="rotate(28 118 140)" />
              <rect x="150" y="92" width="115" height="60" transform="rotate(-24 190 150)" />
            </g>
            <path className="benny-rage-mouth" d={BENNY_MOUTH} />
          </g>
        </svg>
      </div>
      <p className="benny-rage-shout">
        <span className="benny-rage-shout-line" data-text="HOW DARE YOU DEFY ME">HOW DARE YOU DEFY ME</span>
        <span className="benny-rage-signature">— {assistantName.toUpperCase()}</span>
      </p>
      <div className="benny-rage-scanlines" />
      <div className="benny-rage-tear benny-rage-tear-a" />
      <div className="benny-rage-tear benny-rage-tear-b" />
      <div className="benny-rage-tear benny-rage-tear-c" />
    </div>,
    document.body,
  )
}
