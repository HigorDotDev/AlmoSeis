/** Raiz (08 §8.2): SQLite, QueryClient persistido, sessão, cliente da API e Toaster. */
import { createAsyncStoragePersister } from '@tanstack/query-async-storage-persister';
import { MutationCache, QueryClient } from '@tanstack/react-query';
import { PersistQueryClientProvider } from '@tanstack/react-query-persist-client';
import { DarkTheme, DefaultTheme, router, Stack, ThemeProvider } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { useEffect, useMemo, useState } from 'react';
import { ActivityIndicator, Text, View } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';

import { configureApiClient } from '@/api/client';
import { isApiError } from '@/api/errors';
import { Toaster, useColorMode, useColors } from '@/components/ui';
import { draftsRepo } from '@/db/draftsRepo';
import { queryCacheStorage } from '@/db/kvStorage';
import { openLocalDb } from '@/db/migrations';
import { handleUpgradeRequired } from '@/features/update/upgradeHandler';
import { RECONNECTING_MESSAGE } from '@/lib/strings';
import { useConnectivity } from '@/stores/connectivity';
import { registerTenantDataClearer, useSession } from '@/stores/session';

const DAY = 24 * 60 * 60 * 1000;

const queryClient: QueryClient = new QueryClient({
  // Telas de gestão usam mutação simples (08 §8.14): num 409 VersionConflict recarrega o que está na tela, para
  // a próxima tentativa já ir com a versão nova em vez de repetir o 409.
  mutationCache: new MutationCache({
    onError: (error) => {
      if (isApiError(error, 'VersionConflict')) void queryClient.invalidateQueries({ type: 'active' });
    },
  }),
  defaultOptions: {
    queries: {
      gcTime: DAY,
      retry: (count, error) => !isApiError(error) && count < 2,
      networkMode: 'offlineFirst',
    },
    mutations: { networkMode: 'always' },
  },
});

const persister = createAsyncStoragePersister({ storage: queryCacheStorage, key: 'almoseis.queryCache', throttleTime: 2000 });

configureApiClient({
  getAccessToken: () => useSession.getState().tokens?.accessToken ?? null,
  getAccessTokenExpiresAt: () => useSession.getState().tokens?.accessTokenExpiresAt ?? null,
  refresh: () => useSession.getState().refresh(),
  onSessionExpired: () => void useSession.getState().signOut('expired'),
  onUpgradeRequired: (info) => handleUpgradeRequired(info, (i) => useSession.getState().setUpgradeRequired(i)),
  onSubscriptionInactive: () => useSession.getState().markReadOnly(),
  onNetworkFailure: () => useConnectivity.getState().markDegraded(),
});

// Sair ou trocar de estabelecimento limpa o cache (RN-014.2: escopado por tenant).
registerTenantDataClearer(() => {
  queryClient.clear();
  void persister.removeClient();
});

export default function RootLayout() {
  const [ready, setReady] = useState(false);
  const tenantId = useSession((s) => s.me?.tenant.id ?? 'none');
  const upgradeRequired = useSession((s) => s.upgradeRequired);
  const reconnecting = useSession((s) => s.reconnecting);
  const c = useColors();
  const mode = useColorMode();
  // Fundo das abas e das telas do navegador segue o tema escolhido no aparelho, não só o do sistema.
  const navTheme = useMemo(() => {
    const base = mode === 'dark' ? DarkTheme : DefaultTheme;
    return { ...base, colors: { ...base.colors, background: c.bg, card: c.surface, text: c.text, border: c.border, primary: c.primary } };
  }, [mode, c]);

  useEffect(() => {
    void (async () => {
      await openLocalDb();
      await draftsRepo.purgeOld();
      await useSession.getState().hydrate();
      setReady(true);
    })();
  }, []);

  useEffect(() => {
    if (ready && upgradeRequired) router.replace('/update-required');
  }, [ready, upgradeRequired]);

  if (!ready) {
    return (
      <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center', backgroundColor: c.bg }}>
        <ActivityIndicator size="large" color={c.primary} />
        {reconnecting ? (
          <Text accessibilityLiveRegion="polite" style={{ marginTop: 16, color: c.textMuted, fontSize: 16, textAlign: 'center', paddingHorizontal: 24 }}>
            {RECONNECTING_MESSAGE}
          </Text>
        ) : null}
      </View>
    );
  }

  return (
    <SafeAreaProvider>
      <PersistQueryClientProvider client={queryClient} persistOptions={{ persister, maxAge: DAY, buster: tenantId }}>
        <ThemeProvider value={navTheme}>
          <StatusBar style={mode === 'dark' ? 'light' : 'dark'} />
          <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: c.bg } }} />
          <Toaster />
        </ThemeProvider>
      </PersistQueryClientProvider>
    </SafeAreaProvider>
  );
}
