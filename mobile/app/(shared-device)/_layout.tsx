import { Redirect, Stack } from 'expo-router';

import { useSession } from '@/stores/session';

export default function SharedDeviceLayout() {
  const signedIn = useSession((s) => s.status === 'signedIn');
  const device = useSession((s) => s.sharedDevice);
  if (signedIn) return <Redirect href="/" />;
  if (!device) return <Redirect href="/login" />;
  return <Stack screenOptions={{ headerShown: false }} />;
}
