// https://docs.expo.dev/guides/using-eslint/
const { defineConfig } = require('eslint/config');
const expoConfig = require('eslint-config-expo/flat');

module.exports = defineConfig([
  expoConfig,
  {
    ignores: ['dist/*', 'node_modules/*', '.expo/*'],
  },
  {
    // 08 §8.2: telas em app/ só compõem; nada de fetch direto.
    files: ['app/**/*.tsx'],
    rules: {
      'no-restricted-globals': ['error', { name: 'fetch', message: 'Use os hooks de src/features (docs/02-padroes.md §2.3).' }],
    },
  },
]);
