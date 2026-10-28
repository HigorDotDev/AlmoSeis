/** Raiz (08 §8.2): SQLite, QueryClient persistido, sessão, cliente da API e Toaster. */
import { createAsyncStoragePersister } from '@tanstack/query-async-storage-persister';
import { QueryClient } from '@tanstack/react-query';
import { PersistQueryClientProvider } from '@tanstack/react-query-persist-client';
import { router, Stack } from 'expo-router';
import Storage from 'expo-sqlite/kv-store';
import { StatusBar } from 'expo-status-bar';
import { useEffect, useState } from 'react';
import { ActivityIndicator, View } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';

import { configureApiClient } from '@/api/client';
import { isApiError } from '@/api/errors';
import { Toaster, useColors } from '@/components/ui';
import { draftsRepo } from '@/db/draftsRepo';
import { openLocalDb } from '@/db/migrations';
import { useConnectivity } from '@/stores/connectivity';
import { registerTenantDataClearer, useSession } from '@/stores/session';

const DAY = 24 * 60 * 60 * 1000;

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      gcTime: DAY,
      retry: (count, error) => !isApiError(error) && count < 2,
      networkMode: 'offlineFirst',
    },
    mutations: { networkMode: 'always' },
  },
});

const persister = createAsyncStoragePersister({ storage: Storage, key: 'almoseis.queryCache', throttleTime: 2000 });

configureApiClient({
  getAccessToken: () => useSession.getState().tokens?.accessToken ?? null,
  refresh: () => useSession.getState().refresh(),
  onSessionExpired: () => void useSession.getState().signOut('expired'),
  onUpgradeRequired: (info) => useSession.getState().setUpgradeRequired(info),
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
  const c = useColors();

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
      </View>
    );
  }

  return (
    <SafeAreaProvider>
      <PersistQueryClientProvider client={queryClient} persistOptions={{ persister, maxAge: DAY, buster: tenantId }}>
        <StatusBar style="auto" />
        <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: c.bg } }} />
        <Toaster />
      </PersistQueryClientProvider>
    </SafeAreaProvider>
  );
}
