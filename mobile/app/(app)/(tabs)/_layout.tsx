import { TopTabs } from 'expo-router/js-top-tabs';
import { Text } from 'react-native';

import { SubscriptionBanner, font, useColors } from '@/components/ui';
import { AppHeader } from '@/features/shell/AppShell';
import { usePreferences, type TabName } from '@/stores/preferences';
import { hasPermission, useSession } from '@/stores/session';

/** 08 §8.8: quatro abas superiores filtradas por permissão (RF-001); o aparelho lembra a última usada. */
export default function TabsLayout() {
  const c = useColors();
  const me = useSession((s) => s.me);
  const setLastTab = usePreferences((s) => s.setLastTab);
  const canOrders = hasPermission(me, 'orders.view');
  const canProduction = hasPermission(me, 'production.view');
  const canClosing = hasPermission(me, 'closing.view');
  return (
    <>
      <AppHeader />
      <SubscriptionBanner />
      <TopTabs
        screenListeners={({ route }: { route: { name: string } }) => ({ focus: () => setLastTab(route.name as TabName) })}
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
        <TopTabs.Protected guard={canOrders}>
          <TopTabs.Screen name="marmitas" options={{ title: 'Marmitas' }} />
          <TopTabs.Screen name="salao" options={{ title: 'Salão' }} />
        </TopTabs.Protected>
        <TopTabs.Protected guard={canProduction}>
          <TopTabs.Screen name="pedidos" options={{ title: 'Pedidos' }} />
        </TopTabs.Protected>
        <TopTabs.Protected guard={canClosing}>
          <TopTabs.Screen name="fechamento" options={{ title: 'Fechamento' }} />
        </TopTabs.Protected>
      </TopTabs>
    </>
  );
}
