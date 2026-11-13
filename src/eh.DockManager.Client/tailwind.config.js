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
        }
      },
      animation: {
        pulseSoft: 'pulseSoft 1.6s ease-in-out infinite'
      }
    }
  },
  plugins: []
};
