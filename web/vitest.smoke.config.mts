import { defineConfig } from 'vitest/config';

/** Teste de fumaça: roda `next build` de verdade (lento; fora do `npm test`). */
export default defineConfig({
  test: {
    include: ['tests/**/*.smoke.test.ts'],
    environment: 'node',
    testTimeout: 600_000,
    hookTimeout: 600_000,
  },
});
