import { Redirect } from 'expo-router';

import { initialTab, visibleTabs } from '@/features/tenant/establishment';
import { usePreferences } from '@/stores/preferences';
import { useSession } from '@/stores/session';

/**
 * Entrada: sessão → última aba, se ela ainda existe neste estabelecimento (ADR-0055 item 5), senão a primeira
 * visível; aparelho compartilhado sem sessão → seleção de funcionário; senão login.
 */
export default function Index() {
  const status = useSession((s) => s.status);
  const sharedDevice = useSession((s) => s.sharedDevice);
  const me = useSession((s) => s.me);
  const lastTab = usePreferences((s) => s.lastTab);
  if (status === 'signedIn') return <Redirect href={`/${initialTab(lastTab, visibleTabs(me)) ?? 'marmitas'}`} />;
  if (sharedDevice) return <Redirect href="/select-employee" />;
  return <Redirect href="/login" />;
}
