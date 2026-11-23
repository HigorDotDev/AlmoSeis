/** Cabeçalho, menu expansível (épico §2.1, 08 §8.8) e inatividade do aparelho compartilhado. */
import { Ionicons } from '@expo/vector-icons';
import type { NativeStackHeaderProps } from 'expo-router/native-stack';
import { router } from 'expo-router';
import { useEffect, useRef, useState } from 'react';
import { Pressable, View } from 'react-native';

import type { Permission, TenantModule } from '@/api/types';
import { Banner, BottomSheet, IconButton, Row, Text, TOUCH, radius, space, useColorMode, useColors, type IconName } from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useSettings } from '@/features/session/hooks';
import { draftsRepo } from '@/db/draftsRepo';
import { pendingOpsRepo } from '@/db/pendingOpsRepo';
import { unregisterPush } from '@/features/push/registration';
import { confirmAction } from '@/lib/confirm';
import { hasAnyModule, storefrontSections } from '@/features/tenant/establishment';
import { formatBusinessDate } from '@/lib/dates';
import { usePendingOps } from '@/stores/pendingOps';
import { usePreferences } from '@/stores/preferences';
import { hasPermission, useSession } from '@/stores/session';

/** Cabeçalho das telas empilhadas (o recuo do topo já vem do layout, junto da ConnectivityBar). */
export function ScreenHeader({ options, route, back }: NativeStackHeaderProps) {
  const c = useColors();
  const title = typeof options.title === 'string' ? options.title : route.name;
  return (
    <Row style={{ backgroundColor: c.surface, borderBottomWidth: 1, borderBottomColor: c.border, paddingHorizontal: space.xs, minHeight: 56 }}>
      {back ? <IconButton icon="arrow-back" label="Voltar" onPress={() => router.back()} /> : <View style={{ width: space.md }} />}
      <Text variant="subtitle" numberOfLines={1} style={{ flex: 1 }}>
        {title}
      </Text>
    </Row>
  );
}

/** Nome do estabelecimento, situação do expediente, menu e quem está logado. */
export function AppHeader() {
  const c = useColors();
  const me = useSession((s) => s.me);
  const sharedDevice = useSession((s) => s.sharedDevice);
  const signOut = useSession((s) => s.signOut);
  const day = useCurrentBusinessDay();
  const mode = useColorMode();
  const setThemeMode = usePreferences((s) => s.setThemeMode);
  const [menuOpen, setMenuOpen] = useState(false);
  if (!me) return null;
  const open = !!day.data;
  const dayText = day.data ? `Aberto · ${formatBusinessDate(day.data.businessDate)}` : day.isLoading ? '…' : 'Fechado';
  return (
    <>
      {/* Uma linha só: nome + situação do expediente em selo (docs/10-ux.md D9). */}
      <Row style={{ backgroundColor: c.surface, paddingLeft: space.xs, paddingRight: space.sm, minHeight: 52 }}>
        <IconButton icon="menu" label="Abrir menu" onPress={() => setMenuOpen(true)} />
        <Text variant="subtitle" numberOfLines={1} style={{ flex: 1 }}>
          {me.tenant.name}
        </Text>
        <View
          accessibilityLabel={open ? `Expediente de ${formatBusinessDate(day.data!.businessDate)} aberto` : 'Sem expediente aberto'}
          style={{ flexDirection: 'row', alignItems: 'center', gap: space.xs, paddingHorizontal: space.sm, paddingVertical: space.xs, borderRadius: radius.pill, backgroundColor: open ? c.successBg : c.neutralBg }}
        >
          <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: open ? c.success : c.neutral }} />
          <Text variant="small" style={{ color: open ? c.success : c.neutral, fontWeight: '700' }}>
            {dayText}
          </Text>
        </View>
        {/* Escolha guardada no aparelho; vale para todos que usam este aparelho ou navegador. */}
        <IconButton
          icon={mode === 'dark' ? 'sunny-outline' : 'moon-outline'}
          label={mode === 'dark' ? 'Usar tema claro' : 'Usar tema escuro'}
          onPress={() => setThemeMode(mode === 'dark' ? 'light' : 'dark')}
        />
        {sharedDevice ? (
          <Pressable
            onPress={() => void signOut('idle')}
            accessibilityRole="button"
            accessibilityLabel={`Trocar de pessoa. Agora: ${me.employee.displayName}`}
            style={{ minHeight: 48, paddingHorizontal: space.md, borderRadius: radius.md, backgroundColor: c.primary, justifyContent: 'center' }}
          >
            <Text style={{ color: c.primaryText, fontWeight: '700' }}>{me.employee.displayName} · Trocar</Text>
          </Pressable>
        ) : (
          <Pressable onPress={() => router.push('/menu/profile')} accessibilityRole="button" accessibilityLabel={`Perfil de ${me.employee.displayName}`} style={{ padding: space.sm }}>
            <View style={{ width: 36, height: 36, borderRadius: 18, backgroundColor: c.surfaceAlt, alignItems: 'center', justifyContent: 'center' }}>
              <Text style={{ fontWeight: '700' }}>{me.employee.displayName.slice(0, 1).toUpperCase()}</Text>
            </View>
          </Pressable>
        )}
      </Row>
      <MenuSheet visible={menuOpen} onClose={() => setMenuOpen(false)} />
    </>
  );
}

interface MenuEntry {
  label: string;
  icon: IconName;
  href: string;
  permission?: Permission;
  /** Aparece se o estabelecimento tiver **algum** destes módulos (ADR-0055 item 6). Sem a chave, sempre. */
  modules?: TenantModule[];
  /** Regra além de permissão e módulos (ex.: a página de pedidos, que depende de uma combinação de módulos). */
  visible?: (me: Parameters<typeof hasPermission>[0]) => boolean;
}

/** O catálogo de `Product` (pratos, acompanhamentos, bebidas, adicionais) é o da marmita, do PF e do quilo (ADR-0056 item 4). */
const RESTAURANT_CATALOG: TenantModule[] = ['MARMITA', 'PF', 'KILO', 'DAILY_MENU'];

/** Itens do épico §2.1 agrupados por frequência de uso (docs/10-ux.md D10). */
const GROUPS: { title: string; entries: MenuEntry[] }[] = [
  {
    title: 'Operação',
    entries: [
      { label: 'Cardápio do dia', icon: 'today-outline', href: '/menu/menus/today', permission: 'menu.manage', modules: ['DAILY_MENU'] },
      { label: 'Clientes', icon: 'person-circle-outline', href: '/menu/customers', permission: 'orders.view' },
      // EP-002 F-210: ver e compartilhar com `orders.view`; alterar com `storefront.manage`. A página vende marmita
      // (módulo MARMITA) e, desde a fatia 5 do EP-003, o cardápio fixo da lanchonete (FIXED_MENU + DELIVERY).
      { label: 'Página de pedidos', icon: 'globe-outline', href: '/menu/storefront', permission: 'orders.view', visible: (me) => storefrontSections(me).length > 0 },
      { label: 'Rascunhos e pendências', icon: 'document-text-outline', href: '/menu/drafts' },
    ],
  },
  {
    title: 'Cardápio',
    entries: [
      // Cardápio fixo da lanchonete (ADR-0056): ver com `orders.view`, pausar com `catalog.availability`, cadastrar com `catalog.manage`.
      { label: 'Cardápio', icon: 'book-outline', href: '/menu/fixed-menu', permission: 'orders.view', modules: ['FIXED_MENU'] },
      { label: 'Pratos e bebidas', icon: 'restaurant-outline', href: '/menu/products', permission: 'orders.view', modules: RESTAURANT_CATALOG },
      { label: 'Tamanhos de marmita', icon: 'resize-outline', href: '/menu/sizes', permission: 'orders.view', modules: ['MARMITA'] },
      { label: 'Cardápio semanal', icon: 'calendar-outline', href: '/menu/menus/weekly', permission: 'menu.manage', modules: ['DAILY_MENU'] },
      { label: 'Histórico de cardápios', icon: 'albums-outline', href: '/menu/menus/history', permission: 'menu.manage', modules: ['DAILY_MENU'] },
    ],
  },
  {
    title: 'Financeiro',
    entries: [
      { label: 'Histórico de fechamentos', icon: 'archive-outline', href: '/closing/history', permission: 'closing.view' },
      // Preço do quilo e taxa de entrega da marmita (a taxa do delivery da lanchonete é da fatia 4).
      { label: 'Configuração de preços', icon: 'pricetag-outline', href: '/menu/pricing', permission: 'settings.manage', modules: ['KILO', 'MARMITA'] },
      { label: 'Formas de pagamento', icon: 'card-outline', href: '/menu/payment-methods', permission: 'settings.manage' },
      { label: 'Histórico de operações', icon: 'list-outline', href: '/menu/audit', permission: 'audit.view' },
    ],
  },
  {
    title: 'Equipe',
    entries: [
      { label: 'Funcionários', icon: 'people-outline', href: '/menu/employees', permission: 'employees.manage' },
      { label: 'Perfis e permissões', icon: 'shield-checkmark-outline', href: '/menu/roles' },
      { label: 'Aparelhos compartilhados', icon: 'tablet-landscape-outline', href: '/menu/devices', permission: 'devices.manage' },
    ],
  },
  {
    title: 'Conta',
    entries: [
      { label: 'Perfil do usuário', icon: 'person-outline', href: '/menu/profile' },
      { label: 'Dados do estabelecimento', icon: 'storefront-outline', href: '/menu/tenant', permission: 'orders.view' },
      { label: 'Configurações gerais', icon: 'settings-outline', href: '/menu/settings' },
      { label: 'Assinatura e plano', icon: 'ribbon-outline', href: '/menu/subscription', permission: 'subscription.view' },
    ],
  },
];

/** Grupos do menu com o que este funcionário pode e este estabelecimento tem (permissão + módulos). */
export function menuGroups(me: Parameters<typeof hasPermission>[0]) {
  return GROUPS.map((g) => ({
    ...g,
    entries: g.entries.filter((e) => (!e.permission || hasPermission(me, e.permission)) && (!e.modules || hasAnyModule(me, e.modules)) && (!e.visible || e.visible(me))),
  })).filter((g) => g.entries.length > 0);
}

export function MenuSheet({ visible, onClose }: { visible: boolean; onClose: () => void }) {
  const c = useColors();
  const me = useSession((s) => s.me);
  const pending = usePendingOps((s) => s.ops.length);
  const go = (href: string) => {
    onClose();
    router.push(href as never);
  };
  const groups = menuGroups(me);
  return (
    <BottomSheet visible={visible} title="Menu" onClose={onClose}>
      {pending > 0 ? (
        <Banner tone="warning" text={pending === 1 ? '1 operação pendente neste aparelho.' : `${pending} operações pendentes neste aparelho.`} action={{ label: 'Conferir', onPress: () => go('/menu/drafts') }} />
      ) : null}
      {groups.map((g) => (
        <View key={g.title}>
          <Text variant="small" muted style={{ fontWeight: '700', textTransform: 'uppercase', letterSpacing: 0.5, marginBottom: space.xs }}>
            {g.title}
          </Text>
          {g.entries.map((e) => (
            <Pressable
              key={e.href}
              accessibilityRole="button"
              onPress={() => go(e.href)}
              style={({ pressed }) => ({ flexDirection: 'row', alignItems: 'center', gap: space.md, minHeight: TOUCH, opacity: pressed ? 0.6 : 1 })}
            >
              <Ionicons name={e.icon} size={22} color={c.text} />
              <Text style={{ flex: 1 }}>{e.label}</Text>
              {e.href === '/menu/drafts' && pending > 0 ? <Text style={{ color: c.warning, fontWeight: '700' }}>{pending}</Text> : null}
              <Ionicons name="chevron-forward" size={18} color={c.textMuted} />
            </Pressable>
          ))}
        </View>
      ))}
      <Pressable
        accessibilityRole="button"
        onPress={() => {
          onClose();
          confirmSignOut();
        }}
        style={{ flexDirection: 'row', alignItems: 'center', gap: space.md, minHeight: 48 }}
      >
        <Ionicons name="log-out-outline" size={22} color={c.danger} />
        <Text style={{ color: c.danger, fontWeight: '700' }}>Encerrar sessão</Text>
      </Pressable>
    </BottomSheet>
  );
}

/** 08 §8.4: sair apaga rascunhos e pendências só depois de avisar. */
export async function confirmSignOut(): Promise<void> {
  const { me, signOut } = useSession.getState();
  if (!me) return;
  const [drafts, ops] = await Promise.all([draftsRepo.count(me.tenant.id), pendingOpsRepo.list(me.tenant.id)]);
  const doSignOut = async (clear: boolean) => {
    if (clear) {
      await draftsRepo.clearTenant(me.tenant.id);
      await pendingOpsRepo.clearTenant(me.tenant.id);
      usePendingOps.getState().clear();
    }
    // Aparelho pessoal: tira o push antes do logout (ADR-0049). No compartilhado o registro fica com o aparelho.
    if (!useSession.getState().sharedDevice) await unregisterPush();
    await signOut('user');
    router.replace('/');
  };
  if (drafts === 0 && ops.length === 0) {
    confirmAction({ title: 'Encerrar sessão?', confirmText: 'Encerrar', onConfirm: () => void doSignOut(false) });
    return;
  }
  confirmAction({
    title: 'Há itens não enviados',
    message: `${drafts} rascunho(s) e ${ops.length} operação(ões) pendente(s) serão apagados deste aparelho. Confira em "Rascunhos e pendências" antes de sair.`,
    confirmText: 'Sair e apagar',
    onConfirm: () => void doSignOut(true),
  });
}

/** Aparelho compartilhado: sem toque por `sharedDeviceIdleSeconds`, volta para a seleção de funcionário. */
export function useSharedDeviceIdle(): () => void {
  const sharedDevice = useSession((s) => s.sharedDevice);
  const signedIn = useSession((s) => s.status === 'signedIn');
  const settings = useSettings();
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const seconds = settings.data?.sharedDeviceIdleSeconds ?? 120;
  const active = !!sharedDevice && signedIn;

  const touch = () => {
    if (!active) return;
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => void useSession.getState().signOut('idle'), seconds * 1000);
  };

  useEffect(() => {
    touch();
    return () => {
      if (timer.current) clearTimeout(timer.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, seconds]);

  return touch;
}
