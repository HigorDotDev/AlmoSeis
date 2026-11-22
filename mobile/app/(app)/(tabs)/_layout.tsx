import { TopTabs } from 'expo-router/js-top-tabs';
import { Text } from 'react-native';

import { SubscriptionBanner, font, useColors } from '@/components/ui';
import { AppHeader } from '@/features/shell/AppShell';
import { isTabName, visibleTabs } from '@/features/tenant/establishment';
import { usePreferences } from '@/stores/preferences';
import { useSession } from '@/stores/session';

/**
 * 08 §8.8: abas superiores pelos módulos do estabelecimento e pelas permissões (ADR-0055, RF-001); o aparelho
 * lembra a última usada. Com `me` novo (ex.: `TenantModulesChanged`), as abas se remontam sozinhas.
 */
export default function TabsLayout() {
  const c = useColors();
  const me = useSession((s) => s.me);
  const setLastTab = usePreferences((s) => s.setLastTab);
  const tabs = visibleTabs(me);
  const shown = (name: string) => tabs.some((t) => t.name === name);
  const title = (name: string) => tabs.find((t) => t.name === name)?.title ?? '';
  return (
    <>
      <AppHeader />
      <SubscriptionBanner />
      <TopTabs
        screenListeners={({ route }: { route: { name: string } }) => ({ focus: () => isTabName(route.name) && setLastTab(route.name) })}
        screenOptions={{
          tabBarStyle: { backgroundColor: c.surface },
          tabBarActiveTintColor: c.primary,
          tabBarInactiveTintColor: c.textMuted,
          tabBarIndicatorStyle: { backgroundColor: c.primary, height: 3 },
          // Uma linha só, mesmo com a fonte do sistema grande: "Fechamento" encolhe em vez de quebrar.
          tabBarLabel: ({ color, children }) => (
            <Text numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.75} maxFontSizeMultiplier={1.3} style={{ color, fontWeight: '700', fontSize: font.sm }}>
              {children}
            </Text>
          ),
          tabBarScrollEnabled: false,
        }}
      >
        <TopTabs.Protected guard={shown('marmitas')}>
          <TopTabs.Screen name="marmitas" options={{ title: title('marmitas') }} />
        </TopTabs.Protected>
        <TopTabs.Protected guard={shown('salao')}>
          <TopTabs.Screen name="salao" options={{ title: title('salao') }} />
        </TopTabs.Protected>
        <TopTabs.Protected guard={shown('delivery')}>
          <TopTabs.Screen name="delivery" options={{ title: title('delivery') }} />
        </TopTabs.Protected>
        {/* Rota `pedidos` para todos; o título é "Pedidos" ou "Cozinha" (ADR-0055 item 5). */}
        <TopTabs.Protected guard={shown('pedidos')}>
          <TopTabs.Screen name="pedidos" options={{ title: title('pedidos') }} />
        </TopTabs.Protected>
        <TopTabs.Protected guard={shown('fechamento')}>
          <TopTabs.Screen name="fechamento" options={{ title: title('fechamento') }} />
        </TopTabs.Protected>
      </TopTabs>
    </>
  );
}
