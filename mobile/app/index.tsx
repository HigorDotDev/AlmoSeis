import { Redirect } from 'expo-router';

import { usePreferences } from '@/stores/preferences';
import { useSession } from '@/stores/session';

/** Entrada: sessão → última aba; aparelho compartilhado sem sessão → seleção de funcionário; senão login. */
export default function Index() {
  const status = useSession((s) => s.status);
  const sharedDevice = useSession((s) => s.sharedDevice);
  const lastTab = usePreferences((s) => s.lastTab);
  if (status === 'signedIn') return <Redirect href={`/${lastTab ?? 'marmitas'}`} />;
  if (sharedDevice) return <Redirect href="/select-employee" />;
  return <Redirect href="/login" />;
}
