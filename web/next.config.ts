import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  // Imagem Docker enxuta (web/Dockerfile, docs/09-devops.md §9.4): `.next/standalone/server.js` sem node_modules inteiro.
  output: 'standalone',
  poweredByHeader: false,
  reactStrictMode: true,
  async headers() {
    return [
      {
        source: '/:path*',
        headers: [
          { key: 'X-Content-Type-Options', value: 'nosniff' },
          { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
          { key: 'X-Frame-Options', value: 'DENY' },
        ],
      },
      {
        // Acompanhamento (ADR-0047 item 9): o token está no caminho; sem cache, sem indexação e sem Referer.
        source: '/:slug/pedido/:token',
        headers: [
          { key: 'Cache-Control', value: 'private, no-store' },
          { key: 'Referrer-Policy', value: 'no-referrer' },
          { key: 'X-Robots-Tag', value: 'noindex' },
        ],
      },
    ];
  },
};

export default nextConfig;
