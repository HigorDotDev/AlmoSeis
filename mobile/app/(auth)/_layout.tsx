import { Redirect, Stack } from 'expo-router';

import { useSession } from '@/stores/session';

/** Com sessão, as telas de entrada não fazem sentido. */
export default function AuthLayout() {
  const signedIn = useSession((s) => s.status === 'signedIn');
  if (signedIn) return <Redirect href="/" />;
  return <Stack screenOptions={{ headerShown: false }} />;
}
