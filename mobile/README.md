# AlmoSeis — app mobile

App Expo (SDK 58, React Native 0.88, New Architecture, Hermes) descrito em `../docs/08-mobile.md`.

## Rodar no desenvolvimento

Requisitos: **Node ≥ 22.13** (o SDK 58 não roda no Node 21), Android Studio com um emulador ou um aparelho
Android com depuração USB.

```bash
npm ci
cp .env.example .env            # EXPO_PUBLIC_API_BASE_URL=http://<ip-da-máquina>:5080
npx expo run:android            # primeira vez: gera e instala o Dev Client
npm start                       # depois: só o Metro
```

O app usa módulos nativos fora do Expo Go (SQLite, SecureStore, áudio, image picker), então precisa do Dev Client.
A API local sobe com `docker compose up -d` + `dotnet run` (09 §9.2).

## Verificações (as mesmas do `mobile-ci.yml`, 09 §9.4)

```bash
npx tsc --noEmit
npx expo lint
npx jest
```

## Estrutura

- `app/`: rotas do Expo Router. Só reexportam telas de `src/features/*/screens` (08 §8.2).
- `src/api/`: `client.ts` (headers, refresh single-flight, 426/402, timeout), `critical.ts` (escritas [I]/[M] como dados),
  `endpoints/*` (um arquivo por recurso do 06), `types.ts` (escritos à mão a partir do 06), `queryKeys.ts`.
- `src/features/sync/`: `operations.ts` (ciclo da escrita crítica e resultado desconhecido), `useCriticalMutation.ts`,
  `syncEngine.ts` (`/sync/changes`), `useAppRuntime.ts` (conectividade, SignalR, primeiro/segundo plano).
- `src/db/`: SQLite (rascunhos, pendências, metadados). Tokens ficam só no SecureStore (`src/stores/secure.ts`).
- `e2e/`: fluxos Maestro de 08 §8.13 (rodam em homolog, com o emulador).

## Builds (EAS)

Perfis em `eas.json` (`development`, `preview` → homolog, `production` → lojas). Falta vincular o projeto ao EAS
(`npx eas-cli@latest init`) e configurar o `EXPO_TOKEN` nos secrets do repositório.
