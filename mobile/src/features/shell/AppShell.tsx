/** Cabeçalho, menu expansível (épico §2.1, 08 §8.8) e inatividade do aparelho compartilhado. */
import { Ionicons } from '@expo/vector-icons';
import type { NativeStackHeaderProps } from 'expo-router/native-stack';
import { router } from 'expo-router';
import { useEffect, useRef, useState } from 'react';
import { Alert, Pressable, View } from 'react-native';

import type { Permission } from '@/api/types';
import { BottomSheet, IconButton, Row, Text, radius, space, useColors, type IconName } from '@/components/ui';
import { useCurrentBusinessDay } from '@/features/businessDay/hooks';
import { useSettings } from '@/features/session/hooks';
import { draftsRepo } from '@/db/draftsRepo';
import { pendingOpsRepo } from '@/db/pendingOpsRepo';
import { formatBusinessDate } from '@/lib/dates';
import { usePendingOps } from '@/stores/pendingOps';
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
  const [menuOpen, setMenuOpen] = useState(false);
  if (!me) return null;
  const dayText = day.data ? `Expediente ${formatBusinessDate(day.data.businessDate)} · aberto` : day.isLoading ? '…' : 'Sem expediente';
  return (
    <>
      <Row style={{ backgroundColor: c.surface, paddingHorizontal: space.xs, minHeight: 56 }}>
        <IconButton icon="menu" label="Abrir menu" onPress={() => setMenuOpen(true)} />
        <View style={{ flex: 1 }}>
          <Text variant="subtitle" numberOfLines={1}>
            {me.tenant.name}
          </Text>
          <Text variant="small" style={{ color: day.data ? c.success : c.textMuted }}>
            {dayText}
          </Text>
        </View>
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
}

const ENTRIES: MenuEntry[] = [
  { label: 'Perfil do usuário', icon: 'person-outline', href: '/menu/profile' },
  { label: 'Dados do estabelecimento', icon: 'storefront-outline', href: '/menu/tenant', permission: 'orders.view' },
  { label: 'Funcionários', icon: 'people-outline', href: '/menu/employees', permission: 'employees.manage' },
  { label: 'Perfis e permissões', icon: 'shield-checkmark-outline', href: '/menu/roles' },
  { label: 'Pratos e bebidas', icon: 'restaurant-outline', href: '/menu/products', permission: 'orders.view' },
  { label: 'Tamanhos de marmita', icon: 'resize-outline', href: '/menu/sizes', permission: 'orders.view' },
  { label: 'Cardápio do dia', icon: 'today-outline', href: '/menu/menus/today', permission: 'menu.manage' },
  { label: 'Cardápio semanal', icon: 'calendar-outline', href: '/menu/menus/weekly', permission: 'menu.manage' },
  { label: 'Histórico de cardápios', icon: 'albums-outline', href: '/menu/menus/history', permission: 'menu.manage' },
  { label: 'Clientes', icon: 'person-circle-outline', href: '/menu/customers', permission: 'orders.view' },
  { label: 'Configuração de preços', icon: 'pricetag-outline', href: '/menu/pricing', permission: 'settings.manage' },
  { label: 'Formas de pagamento', icon: 'card-outline', href: '/menu/payment-methods', permission: 'settings.manage' },
  { label: 'Histórico de operações', icon: 'list-outline', href: '/menu/audit', permission: 'audit.view' },
  { label: 'Histórico de fechamentos', icon: 'archive-outline', href: '/closing/history', permission: 'closing.view' },
  { label: 'Configurações gerais', icon: 'settings-outline', href: '/menu/settings' },
  { label: 'Aparelhos compartilhados', icon: 'tablet-landscape-outline', href: '/menu/devices', permission: 'devices.manage' },
  { label: 'Rascunhos e pendências', icon: 'document-text-outline', href: '/menu/drafts' },
  { label: 'Assinatura e plano', icon: 'ribbon-outline', href: '/menu/subscription', permission: 'subscription.view' },
];

export function MenuSheet({ visible, onClose }: { visible: boolean; onClose: () => void }) {
  const c = useColors();
  const me = useSession((s) => s.me);
  const pending = usePendingOps((s) => s.ops.length);
  const entries = ENTRIES.filter((e) => !e.permission || hasPermission(me, e.permission));
  return (
    <BottomSheet visible={visible} title="Menu" onClose={onClose}>
      {entries.map((e) => (
        <Pressable
          key={e.href}
          accessibilityRole="button"
          onPress={() => {
            onClose();
            router.push(e.href as never);
          }}
          style={({ pressed }) => ({ flexDirection: 'row', alignItems: 'center', gap: space.md, minHeight: 48, opacity: pressed ? 0.6 : 1 })}
        >
          <Ionicons name={e.icon} size={22} color={c.text} />
          <Text style={{ flex: 1 }}>{e.label}</Text>
          {e.href === '/menu/drafts' && pending > 0 ? <Text style={{ color: c.warning, fontWeight: '700' }}>{pending}</Text> : null}
        </Pressable>
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
    await signOut('user');
    router.replace('/');
  };
  if (drafts === 0 && ops.length === 0) {
    Alert.alert('Encerrar sessão?', undefined, [
      { text: 'Voltar', style: 'cancel' },
      { text: 'Encerrar', style: 'destructive', onPress: () => void doSignOut(false) },
    ]);
    return;
  }
  Alert.alert(
    'Há itens não enviados',
    `${drafts} rascunho(s) e ${ops.length} operação(ões) pendente(s) serão apagados deste aparelho. Confira em "Rascunhos e pendências" antes de sair.`,
    [
      { text: 'Voltar', style: 'cancel' },
      { text: 'Sair e apagar', style: 'destructive', onPress: () => void doSignOut(true) },
    ],
  );
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
