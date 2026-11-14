// File: src/eh.DockManager.Client/tailwind.config.js
/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        ink: {
          950: '#06080d',
          900: '#0b0f17',
          850: '#10151f',
          800: '#161c28',
          700: '#232b3b',
          600: '#39435a'
        },
        accent: {
          DEFAULT: '#38bdf8',
          soft: '#0ea5e9',
          muted: '#1e3a5f'
        },
        ok: '#34d399',
        warn: '#fbbf24',
        bad: '#f87171'
      },
      fontFamily: {
        mono: ['ui-monospace', 'SFMono-Regular', 'Menlo', 'Consolas', 'monospace']
      },
      keyframes: {
        pulseSoft: {
          '0%, 100%': { opacity: '1' },
          '50%': { opacity: '0.35' }
        },
        // `translate`/`scale` are independent CSS properties, so they animate without clobbering
        // the Tailwind `-translate-x-1/2` used to centre the toast on wide screens.
        toastIn: {
          '0%': { opacity: '0', translate: '0 1rem', scale: '0.97' },
          '100%': { opacity: '1', translate: '0 0', scale: '1' }
        },
        toastOut: {
          '0%': { opacity: '1', translate: '0 0', scale: '1' },
          '100%': { opacity: '0', translate: '0 0.85rem', scale: '0.98' }
        }
      },
      animation: {
        pulseSoft: 'pulseSoft 1.6s ease-in-out infinite',
        toastIn: 'toastIn 320ms cubic-bezier(0.22, 1, 0.36, 1) both',
        toastOut: 'toastOut 220ms ease-in both'
      }
    }
  },
  plugins: []
};
