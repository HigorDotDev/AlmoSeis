import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const root = fileURLToPath(new URL('..', import.meta.url));

/** Todo o JavaScript do navegador gerado pelo build. */
function clientJs(): string {
  const dir = join(root, '.next', 'static', 'chunks');
  const out: string[] = [];
  const walk = (d: string) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      if (e.isDirectory()) walk(join(d, e.name));
      else if (e.name.endsWith('.js')) out.push(readFileSync(join(d, e.name), 'utf8'));
    }
  };
  walk(dir);
  return out.join('\n');
}

/** Valor de `WEB_RENDERER_KEY` só deste build: se aparecer no JavaScript do navegador, a chave vazou. */
const rendererKey = `smoke-renderer-${randomBytes(16).toString('hex')}`;

describe('next build', () => {
  it('compila a página sem erros e gera as rotas dinâmicas', () => {
    const result = spawnSync('npx next build', {
      cwd: root,
      encoding: 'utf8',
      shell: true,
      env: { ...process.env, NEXT_TELEMETRY_DISABLED: '1', WEB_RENDERER_KEY: rendererKey },
    });
    if (result.status !== 0) {
      console.error(result.stdout, result.stderr);
    }
    expect(result.status).toBe(0);
    expect(existsSync(join(root, '.next', 'BUILD_ID'))).toBe(true);
    const manifest = readFileSync(join(root, '.next', 'app-path-routes-manifest.json'), 'utf8');
    expect(manifest).toContain('/[slug]');
    expect(manifest).toContain('/[slug]/carrinho');
    expect(manifest).toContain('/[slug]/pedido/[token]');

    // O navegador fala direto com as rotas públicas (ADR-0051 item 2): envio, cotação da entrega e acompanhamento.
    const js = clientJs();
    expect(js).toContain('/delivery-quotes');
    expect(js).toContain('/orders');
    expect(js).toContain('/withdraw');
    // A chave do servidor da página nunca vai para o navegador: nem o nome do header, nem o valor usado no build.
    expect(js).not.toContain('X-Web-Renderer-Key');
    expect(js).not.toContain(rendererKey);
  });
});
