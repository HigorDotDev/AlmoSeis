import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const root = fileURLToPath(new URL('..', import.meta.url));

describe('next build', () => {
  it('compila a página sem erros e gera as rotas dinâmicas', () => {
    const result = spawnSync('npx next build', {
      cwd: root,
      encoding: 'utf8',
      shell: true,
      env: { ...process.env, NEXT_TELEMETRY_DISABLED: '1' },
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
  });
});
