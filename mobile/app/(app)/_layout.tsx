import { Redirect, Stack } from 'expo-router';
import { View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { ConnectivityBar, useColors } from '@/components/ui';
import { useMeSync } from '@/features/session/hooks';
import { ScreenHeader, useSharedDeviceIdle } from '@/features/shell/AppShell';
import { useAppRuntime } from '@/features/sync/useAppRuntime';
import { useSession } from '@/stores/session';

/** 08 §8.8: guarda de sessão, barra de conectividade, sync/tempo real e inatividade do aparelho compartilhado. */
export default function AppLayout() {
  const status = useSession((s) => s.status);
  const sharedDevice = useSession((s) => s.sharedDevice);
  if (status !== 'signedIn') return <Redirect href={sharedDevice ? '/select-employee' : '/login'} />;
  return <SignedIn />;
}

function SignedIn() {
  const c = useColors();
  const insets = useSafeAreaInsets();
  useAppRuntime();
  useMeSync();
  const touch = useSharedDeviceIdle();
  return (
    <View style={{ flex: 1, paddingTop: insets.top, backgroundColor: c.surface }} onTouchStart={touch}>
      <ConnectivityBar />
      <Stack screenOptions={{ header: (props) => <ScreenHeader {...props} />, contentStyle: { backgroundColor: c.bg } }}>
        <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
      </Stack>
    </View>
  );
}
