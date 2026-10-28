/** 08 §8.10: perfil, estabelecimento, preços, formas de pagamento, auditoria, configurações, assinatura, rascunhos. */
import * as Application from 'expo-application';
import { router, Stack } from 'expo-router';
import { useCallback, useEffect, useState } from 'react';
import { Alert, Switch, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { PaymentMethod, Tenant, TenantSettings } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  Chip,
  EmptyState,
  KeyValue,
  MoneyInput,
  QuantityStepper,
  Row,
  Screen,
  Section,
  Skeleton,
  SyncStatePill,
  Text,
  TextField,
} from '@/components/ui';
import { draftsRepo } from '@/db/draftsRepo';
import type { Draft, PendingOperation } from '@/db/schema';
import { useAuditLogs } from '@/features/employees/hooks';
import { useSetPin, useSettings, useSubscription, useSwitchTenant, useTenant, useUpdateSettings, useUpdateTenant } from '@/features/session/hooks';
import { confirmSignOut } from '@/features/shell/AppShell';
import { discardPending, isExpired, resendPending, resolveUnknown } from '@/features/sync/operations';
import { formatDateTime } from '@/lib/dates';
import { formatBRL, toCents } from '@/lib/money';
import { paymentMethodNames, roleNames } from '@/lib/strings';
import { usePendingOps } from '@/stores/pendingOps';
import { usePreferences } from '@/stores/preferences';
import { useCanWrite, useSession } from '@/stores/session';
import { toast } from '@/stores/toast';

export function ProfileScreen() {
  const me = useSession((s) => s.me)!;
  const switchTenant = useSwitchTenant();
  const setPin = useSetPin();
  const [pin, setPinValue] = useState('');
  const [password, setPassword] = useState('');
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Perfil' }} />
      <Card>
        <Text variant="subtitle">{me.user.fullName}</Text>
        <Text muted>
          {me.employee.displayName} · {me.employee.roles.map((r) => roleNames[r]).join(', ')}
        </Text>
        <Text variant="small" muted>
          {me.user.email ?? me.user.phoneNumber}
        </Text>
      </Card>

      <Section title={me.employee.hasPin ? 'Trocar PIN' : 'Definir PIN'}>
        <Text muted>O PIN (4 a 6 dígitos) serve para entrar nos aparelhos compartilhados.</Text>
        <TextField label="Novo PIN" value={pin} onChangeText={(t) => setPinValue(t.replace(/\D/g, '').slice(0, 6))} keyboardType="number-pad" secureTextEntry />
        <TextField label="Sua senha atual" value={password} onChangeText={setPassword} secureTextEntry />
        {setPin.error ? <Banner tone="danger" text={errorMessage(setPin.error)} /> : null}
        <Button
          title="Salvar PIN"
          disabled={pin.length < 4 || !password}
          loading={setPin.isPending}
          onPress={() => setPin.mutate({ pin, currentPassword: password }, { onSuccess: () => { setPinValue(''); setPassword(''); toast('PIN salvo'); } })}
        />
      </Section>

      <Section title="Senha">
        <Text muted>Para trocar a senha, use “Esqueci a senha” no login (com e-mail) ou peça um código ao proprietário.</Text>
      </Section>

      {me.memberships.length > 1 ? (
        <Section title="Trocar de estabelecimento">
          {switchTenant.error ? <Banner tone="danger" text={errorMessage(switchTenant.error)} /> : null}
          {me.memberships.map((m) => (
            <Card key={m.tenantId} onPress={m.tenantId === me.tenant.id ? undefined : () => switchTenant.mutate(m.tenantId, { onSuccess: () => router.replace('/') })}>
              <Text style={{ fontWeight: m.tenantId === me.tenant.id ? '700' : '400' }}>
                {m.tenantName}
                {m.tenantId === me.tenant.id ? ' (atual)' : ''}
              </Text>
              <Text variant="small" muted>
                {m.roles.map((r) => roleNames[r]).join(', ')}
              </Text>
            </Card>
          ))}
        </Section>
      ) : null}
      <Button title="Encerrar sessão" variant="danger" onPress={() => void confirmSignOut()} />
    </Screen>
  );
}

export function TenantScreen() {
  const tenant = useTenant();
  if (!tenant.data) return <Skeleton lines={4} />;
  return <TenantForm key={tenant.data.version} tenant={tenant.data} />;
}

function TenantForm({ tenant: t }: { tenant: Tenant }) {
  const canEdit = useCanWrite('settings.manage');
  const update = useUpdateTenant();
  const [name, setName] = useState(t.name);
  const [city, setCity] = useState(t.city);
  const [state, setState] = useState(t.state);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Estabelecimento' }} />
      <TextField label="Nome" value={name} onChangeText={setName} editable={canEdit} />
      <KeyValue label={t.documentType} value={t.documentNumber} />
      <TextField label="Cidade" value={city} onChangeText={setCity} editable={canEdit} />
      <TextField label="UF" value={state} onChangeText={(s) => setState(s.toUpperCase().slice(0, 2))} editable={canEdit} />
      <KeyValue label="Fuso horário" value={t.timeZone} />
      {update.error ? <Banner tone="danger" text={errorMessage(update.error)} /> : null}
      {canEdit ? (
        <Button title="Salvar" loading={update.isPending} onPress={() => update.mutate({ version: t.version, body: { name: name.trim(), city: city.trim(), state } }, { onSuccess: () => toast('Dados salvos') })} />
      ) : null}
    </Screen>
  );
}

export function PricingScreen() {
  const settings = useSettings();
  if (!settings.data) return <Skeleton />;
  return <PricingForm key={settings.data.version} settings={settings.data} />;
}

function PricingForm({ settings: st }: { settings: TenantSettings }) {
  const update = useUpdateSettings();
  const [kilo, setKilo] = useState<string>(st.kiloPricePerKg ?? '0.00');
  const [fee, setFee] = useState<string>(st.defaultDeliveryFee);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Preços' }} />
      <MoneyInput label="Preço do quilo" value={kilo} onChange={setKilo} />
      <Text variant="small" muted>
        Mudar o preço não altera pesagens já registradas (RN-006.3).
      </Text>
      <MoneyInput label="Taxa de entrega padrão" value={fee} onChange={setFee} />
      {update.error ? <Banner tone="danger" text={errorMessage(update.error)} /> : null}
      <Button
        title="Salvar preços"
        loading={update.isPending}
        disabled={toCents(kilo) === 0}
        onPress={() => update.mutate({ version: st.version, body: { kiloPricePerKg: kilo, defaultDeliveryFee: fee } }, { onSuccess: () => toast('Preços salvos') })}
      />
    </Screen>
  );
}

const METHODS: PaymentMethod[] = ['CASH', 'PIX', 'CARD'];

export function PaymentMethodsScreen() {
  const settings = useSettings();
  if (!settings.data) return <Skeleton />;
  return <PaymentMethodsForm key={settings.data.version} settings={settings.data} />;
}

function PaymentMethodsForm({ settings: st }: { settings: TenantSettings }) {
  const update = useUpdateSettings();
  const [enabled, setEnabled] = useState<PaymentMethod[]>(st.enabledPaymentMethods);
  const [idle, setIdle] = useState(st.sharedDeviceIdleSeconds);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Formas de pagamento' }} />
      <Row style={{ flexWrap: 'wrap' }}>
        {METHODS.map((m) => (
          <Chip key={m} label={paymentMethodNames[m]} selected={enabled.includes(m)} onPress={() => setEnabled(enabled.includes(m) ? enabled.filter((x) => x !== m) : [...enabled, m])} />
        ))}
      </Row>
      <Section title="Aparelho compartilhado">
        <Row style={{ justifyContent: 'space-between' }}>
          <Text style={{ flex: 1 }}>Voltar para a seleção após (minutos sem uso)</Text>
          <QuantityStepper label="Minutos" min={1} max={60} value={Math.round(idle / 60)} onChange={(m) => setIdle(m * 60)} />
        </Row>
      </Section>
      {update.error ? <Banner tone="danger" text={errorMessage(update.error)} /> : null}
      <Button
        title="Salvar"
        disabled={enabled.length === 0}
        loading={update.isPending}
        onPress={() => update.mutate({ version: st.version, body: { enabledPaymentMethods: enabled, sharedDeviceIdleSeconds: idle } }, { onSuccess: () => toast('Configurações salvas') })}
      />
    </Screen>
  );
}

const operationText: Record<string, string> = {
  PaymentRegistered: 'registrou pagamento',
  PaymentReversed: 'estornou',
  OrderCreated: 'criou o pedido',
  OrderCanceled: 'cancelou o pedido',
  OrderItemAdded: 'incluiu item',
  OrderItemRemoved: 'removeu item',
  OrderItemChanged: 'alterou item',
  OrderStatusChanged: 'mudou o status',
  BusinessDayOpened: 'abriu o expediente',
  DailyClosingFinalized: 'fechou o expediente',
  CashMovementRegistered: 'registrou movimentação de caixa',
};

export function AuditScreen() {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const [entityType, setEntityType] = useState<string | undefined>(undefined);
  const logs = useAuditLogs({ entityType });
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Histórico de operações' }} />
      <Row style={{ flexWrap: 'wrap' }}>
        {[undefined, 'ORDER', 'PAYMENT', 'BUSINESS_DAY', 'DAILY_CLOSING', 'PRODUCT', 'EMPLOYEE'].map((t) => (
          <Chip key={t ?? 'all'} label={t ?? 'Tudo'} selected={entityType === t} onPress={() => setEntityType(t)} />
        ))}
      </Row>
      {logs.isLoading ? <Skeleton /> : null}
      {logs.data?.items.length === 0 ? <EmptyState title="Nada no período" /> : null}
      {logs.data?.items.map((l) => {
        const amount = (l.after?.amount ?? l.before?.amount) as string | undefined;
        return (
          <Card key={l.id}>
            <Text>
              {l.actor?.displayName ?? (l.origin === 'PLATFORM_ADMIN' ? 'Suporte AlmoSeis' : 'Sistema')} {operationText[l.operationType] ?? l.operationType}
              {amount ? ` ${formatBRL(amount)}` : ''}
            </Text>
            <Text variant="small" muted>
              {l.entityType} · {formatDateTime(l.occurredAt, timeZone)}
              {l.sharedDevice ? ` · ${l.sharedDevice.name}` : ''}
            </Text>
          </Card>
        );
      })}
    </Screen>
  );
}

export function GeneralSettingsScreen() {
  const prefs = usePreferences();
  const sharedDevice = useSession((s) => s.sharedDevice);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Configurações' }} />
      <Row style={{ justifyContent: 'space-between' }}>
        <Text style={{ flex: 1 }}>Som de pedidos na aba Pedidos</Text>
        <Switch value={prefs.orderSound} onValueChange={prefs.setOrderSound} accessibilityLabel="Som de pedidos" />
      </Row>
      <Row style={{ justifyContent: 'space-between' }}>
        <Text style={{ flex: 1 }}>Tamanho da fonte da cozinha</Text>
        <QuantityStepper label="Fonte da cozinha" min={8} max={20} value={Math.round(prefs.kitchenFontScale * 10)} onChange={(v) => prefs.setKitchenFontScale(v / 10)} />
      </Row>
      <KeyValue label="Aparelho compartilhado" value={sharedDevice ? sharedDevice.name : 'Não'} />
      <KeyValue label="Versão do app" value={`${Application.nativeApplicationVersion ?? '—'} (${Application.nativeBuildVersion ?? '—'})`} />
    </Screen>
  );
}

/** ADR-0017: situação e orientação, sem preço, plano ou link de compra. */
export function SubscriptionScreen() {
  const sub = useSubscription();
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const names = { TRIALING: 'Período de teste', ACTIVE: 'Ativa', PAST_DUE: 'Pagamento pendente', SUSPENDED: 'Suspensa', CANCELED: 'Cancelada' } as const;
  if (!sub.data) return <Skeleton />;
  const s = sub.data;
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Assinatura' }} />
      <Card>
        <Text variant="title">{names[s.status]}</Text>
        {s.daysLeft !== null ? <Text>{s.daysLeft} dia(s) restante(s)</Text> : null}
        {s.status === 'TRIALING' ? <KeyValue label="Teste até" value={formatDateTime(s.trialEndsAt, timeZone)} /> : null}
        {s.currentPeriodEndsAt ? <KeyValue label="Período atual até" value={formatDateTime(s.currentPeriodEndsAt, timeZone)} /> : null}
        {!s.canWrite ? <Banner tone="danger" text="Modo somente leitura." /> : null}
      </Card>
      {s.message ? <Text>{s.message}</Text> : null}
    </Screen>
  );
}

const kindNames: Record<string, string> = {
  NEW_ORDER_MARMITA: 'Nova marmita',
  NEW_ORDER_PF: 'Nova mesa (PF)',
  NEW_ORDER_KILO: 'Nova comanda (quilo)',
  ADD_ITEMS: 'Inclusão de itens',
  PAYMENT: 'Pagamento',
};

const resumeRoute: Record<string, (d: Draft) => string> = {
  NEW_ORDER_MARMITA: () => '/orders/new/marmita',
  NEW_ORDER_PF: () => '/orders/new/pf',
  NEW_ORDER_KILO: () => '/orders/new/kilo',
  ADD_ITEMS: (d) => `/orders/${d.targetId}/add-items`,
  PAYMENT: (d) => `/orders/${d.targetId}/pay`,
};

/** 08 §8.10 `menu/drafts`: rascunhos (continuar/descartar) e pendências (verificar/reenviar/descartar). */
export function DraftsScreen() {
  const me = useSession((s) => s.me)!;
  const ops = usePendingOps((s) => s.ops);
  const [drafts, setDrafts] = useState<Draft[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(() => {
    void draftsRepo.list(me.tenant.id).then(setDrafts);
    void usePendingOps.getState().load(me.tenant.id, me.employee.id);
  }, [me.tenant.id, me.employee.id]);
  useEffect(load, [load]);

  const verify = async (op: PendingOperation) => {
    setBusy(op.idempotencyKey);
    const r = await resolveUnknown(op, { maxInProgressRetries: 1 });
    setBusy(null);
    const messages = {
      completed: 'Confirmada pelo servidor',
      failed: 'Não chegou ao servidor: pode reenviar',
      expired: 'Mais de 72 h: confira no histórico se entrou',
      stillUnknown: 'Ainda sem resposta',
    } as const;
    toast(messages[r.kind], r.kind === 'completed' ? 'success' : 'info');
    load();
  };

  /** Só FAILED dentro das 72 h, com a mesma chave (NOT_FOUND garante que nunca chegou). */
  const resend = async (op: PendingOperation) => {
    setBusy(op.idempotencyKey);
    const outcome = await resendPending(op);
    setBusy(null);
    toast(outcome.kind === 'confirmed' ? 'Enviada' : outcome.kind === 'rejected' || outcome.kind === 'conflict' ? 'Recusada pelo servidor' : 'Ainda sem resposta', outcome.kind === 'confirmed' ? 'success' : 'error');
    load();
  };

  const discard = (op: PendingOperation) =>
    Alert.alert('Descartar pendência?', 'Se ela tiver sido registrada, continuará no servidor.', [
      { text: 'Voltar', style: 'cancel' },
      { text: 'Descartar', style: 'destructive', onPress: () => void discardPending(op).then(load) },
    ]);

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Rascunhos e pendências' }} />
      <Section title="Pendências">
        {ops.length === 0 ? <Text muted>Nenhuma.</Text> : null}
        {ops.map((op) => (
          <Card key={op.idempotencyKey}>
            <Text style={{ fontWeight: '700' }}>{op.description}</Text>
            <Row>
              <SyncStatePill state={op.status === 'SENDING' ? 'sending' : op.status === 'FAILED' ? 'failed' : 'unknown'} />
              <Text variant="small" muted>
                {formatDateTime(op.createdAt, me.tenant.timeZone)}
              </Text>
            </Row>
            {op.status === 'FAILED' ? <Text variant="small">Não chegou ao servidor.</Text> : null}
            {isExpired(op) ? (
              <Text variant="small">
                Mais de 72 h: o app não consegue mais saber se ela entrou. Confira no histórico; se não estiver lá, refaça a operação e descarte esta.
              </Text>
            ) : null}
            <Row style={{ flexWrap: 'wrap' }}>
              {op.status !== 'FAILED' ? <Button title="Verificar" variant="secondary" loading={busy === op.idempotencyKey} onPress={() => void verify(op)} /> : null}
              {op.status === 'FAILED' && !isExpired(op) ? <Button title="Reenviar" loading={busy === op.idempotencyKey} onPress={() => void resend(op)} /> : null}
              <Button title="Descartar" variant="ghost" onPress={() => discard(op)} />
            </Row>
          </Card>
        ))}
      </Section>
      <Section title="Rascunhos">
        {drafts === null ? <Skeleton lines={2} /> : drafts.length === 0 ? <Text muted>Nenhum.</Text> : null}
        {drafts?.map((d) => (
          <Card key={d.id}>
            <Row>
              <SyncStatePill state="draft" />
              <Text style={{ flex: 1 }}>{kindNames[d.kind] ?? d.kind}</Text>
            </Row>
            <Text variant="small" muted>
              {formatDateTime(d.updatedAt, me.tenant.timeZone)}
            </Text>
            <Row>
              <Button title="Continuar" variant="secondary" onPress={() => router.push(resumeRoute[d.kind]!(d) as never)} />
              <Button title="Descartar" variant="ghost" onPress={() => void draftsRepo.remove(d.id).then(load)} />
            </Row>
          </Card>
        ))}
      </Section>
      <View />
    </Screen>
  );
}
