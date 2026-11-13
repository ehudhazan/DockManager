// File: src/eh.DockManager.Client/vite.config.ts
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import { readdirSync, readFileSync, writeFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import * as zlib from 'node:zlib';
import { brotliCompressSync, constants, gzipSync } from 'node:zlib';

/**
 * Build-time pre-compression. Emits .br (Brotli) and .zst (Zstandard, when the Node runtime
 * exposes zstd) siblings next to every served asset so the server can ship them with zero CPU.
 */
function precompress(): Plugin {
  const compressible = /\.(js|mjs|css|html|svg|json|map|txt|xml|wasm)$/i;
  let outDir = 'dist';

  return {
    name: 'eh-precompress',
    apply: 'build',
    configResolved(config) {
      outDir = config.build.outDir;
    },
    closeBundle() {
      const zstdCompress = (zlib as { zstdCompressSync?: (buffer: Buffer, options?: unknown) => Buffer })
        .zstdCompressSync;

      const walk = (dir: string): void => {
        for (const entry of readdirSync(dir)) {
          const full = join(dir, entry);
          if (statSync(full).isDirectory()) {
            walk(full);
            continue;
          }
          if (!compressible.test(entry)) continue;
          const source = readFileSync(full);
          if (source.length < 1024) continue;
          writeFileSync(`${full}.br`, brotliCompressSync(source, {
            params: {
              [constants.BROTLI_PARAM_QUALITY]: 11,
              [constants.BROTLI_PARAM_SIZE_HINT]: source.length
            }
          }));
          writeFileSync(`${full}.gz`, gzipSync(source, { level: 9 }));
          if (zstdCompress) {
            try {
              writeFileSync(`${full}.zst`, zstdCompress(source));
            } catch {
              /* zstd options vary by runtime; skip quietly. */
            }
          }
        }
      };

      try {
        walk(outDir);
        // eslint-disable-next-line no-console
        console.log(`pre-compressed static assets in ${outDir} (.br${zstdCompress ? ' .zst' : ''} .gz)`);
      } catch (error) {
        // eslint-disable-next-line no-console
        console.warn('pre-compression skipped:', error);
      }
    }
  };
}

export default defineConfig({
  plugins: [react(), precompress()],
  build: {
    // Output straight into the ASP.NET Core wwwroot so the server serves the SPA + precompressed siblings.
    outDir: '../eh.DockManager.Server/wwwroot',
    emptyOutDir: true,
    target: 'es2022',
    sourcemap: false,
    chunkSizeWarningLimit: 1500
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'https://localhost:8443', changeOrigin: true, secure: false },
      '/mcp': { target: 'https://localhost:8443', changeOrigin: true, secure: false },
      '/ws': { target: 'wss://localhost:8443', ws: true, secure: false }
    }
  }
});
