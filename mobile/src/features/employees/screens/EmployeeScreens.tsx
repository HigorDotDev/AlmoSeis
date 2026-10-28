/** 08 §8.10: funcionários, convites, perfis e aparelhos compartilhados. */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { Share, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { Employee, Invitation, Role } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  Chip,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  KeyValue,
  Row,
  Screen,
  Section,
  SegmentedControl,
  Skeleton,
  Text,
  TextField,
  space,
} from '@/components/ui';
import { useRegisterSharedDevice } from '@/features/auth/hooks';
import { useRoles } from '@/features/session/hooks';
import { formatDateTime } from '@/lib/dates';
import { formatPhone } from '@/lib/phone';
import { roleNames } from '@/lib/strings';
import { useSession } from '@/stores/session';

import { useDevices, useEmployee, useEmployeeMutations, useEmployees, useInvitations, useInvite, useRevokeDevice, useRevokeInvitation } from '../hooks';

const ROLES: Role[] = ['OWNER', 'MANAGER', 'ATTENDANT', 'KITCHEN', 'CASHIER'];

function RolePicker({ value, onChange }: { value: Role[]; onChange: (r: Role[]) => void }) {
  return (
    <Row style={{ flexWrap: 'wrap' }}>
      {ROLES.map((r) => (
        <Chip key={r} label={roleNames[r]} selected={value.includes(r)} onPress={() => onChange(value.includes(r) ? value.filter((x) => x !== r) : [...value, r])} />
      ))}
    </Row>
  );
}

export function EmployeesScreen() {
  const [status, setStatus] = useState<'ACTIVE' | 'INACTIVE'>('ACTIVE');
  const employees = useEmployees(status);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Funcionários' }} />
      <Button title="Convidar funcionário" icon="person-add-outline" onPress={() => router.push('/menu/employees/invite')} />
      <SegmentedControl
        value={status}
        onChange={setStatus}
        options={[
          { value: 'ACTIVE', label: 'Ativos' },
          { value: 'INACTIVE', label: 'Inativos' },
        ]}
      />
      {employees.isLoading ? <Skeleton /> : null}
      {employees.isError ? <ErrorState message={errorMessage(employees.error)} onRetry={() => void employees.refetch()} /> : null}
      {employees.data?.length === 0 ? <EmptyState title="Ninguém aqui" /> : null}
      {employees.data?.map((e) => (
        <Card key={e.id} onPress={() => router.push(`/menu/employees/${e.id}`)}>
          <Text style={{ fontWeight: '700' }}>{e.displayName}</Text>
          <Text variant="small" muted>
            {e.roles.map((r) => roleNames[r]).join(', ')}
            {e.hasPin ? ' · PIN definido' : ''}
          </Text>
        </Card>
      ))}
    </Screen>
  );
}

export function EmployeeDetailScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const employee = useEmployee(id);
  if (employee.isLoading) return <Skeleton lines={4} />;
  if (!employee.data) return <ErrorState message={errorMessage(employee.error)} onRetry={() => void employee.refetch()} />;
  return <EmployeeDetail employee={employee.data} />;
}

function EmployeeDetail({ employee: e }: { employee: Employee }) {
  const m = useEmployeeMutations();
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const [name, setName] = useState(e.displayName);
  const [roles, setRoles] = useState<Role[]>(e.roles);
  const [confirm, setConfirm] = useState<'deactivate' | 'reactivate' | null>(null);
  const [code, setCode] = useState<Invitation | null>(null);
  const error = m.update.error ?? m.deactivate.error ?? m.reactivate.error ?? m.resetCode.error;
  const active = e.status === 'ACTIVE';

  return (
    <Screen>
      <Stack.Screen options={{ title: e.displayName }} />
      {error ? <Banner tone="danger" text={errorMessage(error)} /> : null}
      {e.contact ? (
        <Card>
          <KeyValue label="Telefone" value={formatPhone(e.contact.phoneNumber) || '—'} />
          <KeyValue label="E-mail" value={e.contact.email ?? '—'} />
          <KeyValue label="Desde" value={formatDateTime(e.joinedAt, timeZone)} />
        </Card>
      ) : null}
      <TextField label="Nome no app" value={name} onChangeText={setName} />
      <Text variant="small" muted>
        Perfis
      </Text>
      <RolePicker value={roles} onChange={setRoles} />
      <Button
        title="Salvar"
        disabled={roles.length === 0 || name.trim().length < 2}
        loading={m.update.isPending}
        onPress={() => m.update.mutate({ employee: e, body: { displayName: name.trim(), roles } })}
      />
      <Button title="Gerar código de redefinição de senha" variant="secondary" loading={m.resetCode.isPending} onPress={() => m.resetCode.mutate(e, { onSuccess: setCode })} />
      {code ? (
        <Card>
          <Text muted>Código de redefinição (passe para {e.displayName}):</Text>
          <Text variant="big" selectable>
            {code.code}
          </Text>
          <Text variant="small" muted>
            Vale até {formatDateTime(code.expiresAt, timeZone)}.
          </Text>
        </Card>
      ) : null}
      {active ? <Button title="Desativar" variant="danger" onPress={() => setConfirm('deactivate')} /> : <Button title="Reativar" variant="secondary" onPress={() => setConfirm('reactivate')} />}
      <ConfirmDialog
        visible={confirm !== null}
        title={confirm === 'deactivate' ? `Desativar ${e.displayName}?` : `Reativar ${e.displayName}?`}
        description={confirm === 'deactivate' ? 'O acesso é revogado em até 30 segundos em todos os aparelhos.' : undefined}
        confirmLabel={confirm === 'deactivate' ? 'Desativar' : 'Reativar'}
        danger={confirm === 'deactivate'}
        loading={m.deactivate.isPending || m.reactivate.isPending}
        onCancel={() => setConfirm(null)}
        onConfirm={() => (confirm === 'deactivate' ? m.deactivate : m.reactivate).mutate(e, { onSettled: () => setConfirm(null) })}
      />
    </Screen>
  );
}

export function InviteScreen() {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const tenantName = useSession((s) => s.me?.tenant.name ?? '');
  const [roles, setRoles] = useState<Role[]>(['ATTENDANT']);
  const invite = useInvite();
  const pending = useInvitations();
  const revoke = useRevokeInvitation();
  const created = invite.data;

  const share = (inv: Invitation) =>
    void Share.share({ message: `Você foi convidado para usar o AlmoSeis em ${tenantName}. Baixe o app e use o código ${inv.code} ou abra ${inv.link}` });

  return (
    <Screen>
      <Stack.Screen options={{ title: 'Convidar funcionário' }} />
      <Text variant="small" muted>
        Perfis do convidado
      </Text>
      <RolePicker value={roles} onChange={setRoles} />
      {invite.error ? <Banner tone="danger" text={errorMessage(invite.error)} /> : null}
      <Button title="Gerar convite" disabled={roles.length === 0} loading={invite.isPending} onPress={() => invite.mutate(roles)} />
      {created ? (
        <Card>
          <Text muted>Código do convite</Text>
          <Text variant="big" selectable style={{ letterSpacing: 4 }}>
            {created.code}
          </Text>
          <Text variant="small" selectable>
            {created.link}
          </Text>
          <Text variant="small" muted>
            Vale até {formatDateTime(created.expiresAt, timeZone)}
          </Text>
          <Button title="Enviar pelo WhatsApp" icon="logo-whatsapp" onPress={() => share(created)} />
        </Card>
      ) : null}
      <Section title="Convites pendentes">
        {pending.data?.length === 0 ? <Text muted>Nenhum.</Text> : null}
        {pending.data?.map((i) => (
          <Card key={i.id}>
            <Row style={{ justifyContent: 'space-between' }}>
              <View>
                <Text style={{ fontWeight: '700' }}>{i.code}</Text>
                <Text variant="small" muted>
                  {i.roles.map((r) => roleNames[r]).join(', ')} · até {formatDateTime(i.expiresAt, timeZone)}
                </Text>
              </View>
              <Button title="Revogar" variant="ghost" loading={revoke.isPending} onPress={() => revoke.mutate(i.id)} />
            </Row>
          </Card>
        ))}
      </Section>
    </Screen>
  );
}

export function RolesScreen() {
  const roles = useRoles();
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Perfis e permissões' }} />
      {roles.isLoading ? <Skeleton /> : null}
      {roles.data?.map((r) => (
        <Card key={r.role}>
          <Text variant="subtitle">{r.name}</Text>
          <Text variant="small" muted>
            {r.permissions.map((p) => permissionText[p] ?? p).join(' · ')}
          </Text>
        </Card>
      ))}
    </Screen>
  );
}

const permissionText: Record<string, string> = {
  'orders.view': 'ver pedidos',
  'orders.create': 'registrar pedidos',
  'orders.edit': 'alterar pedidos',
  'orders.cancel': 'cancelar pedidos',
  'orders.delivery-fee.edit': 'mudar taxa de entrega',
  'production.view': 'ver a cozinha',
  'production.update': 'mudar status de produção',
  'payments.register': 'receber pagamentos',
  'payments.reverse': 'estornar pagamentos',
  'cash.open': 'abrir expediente',
  'cash.movement': 'suprimento e retirada',
  'closing.view': 'ver fechamento',
  'closing.finalize': 'fechar expediente',
  'closing.adjust': 'ajustar fechamento',
  'menu.manage': 'montar cardápios',
  'catalog.manage': 'cadastrar pratos e bebidas',
  'customers.manage': 'cadastrar clientes',
  'employees.manage': 'gerenciar funcionários',
  'devices.manage': 'aparelhos compartilhados',
  'settings.manage': 'configurações',
  'audit.view': 'histórico de operações',
  'subscription.view': 'assinatura',
};

export function DevicesScreen() {
  const timeZone = useSession((s) => s.me?.tenant.timeZone ?? 'America/Sao_Paulo');
  const devices = useDevices();
  const revoke = useRevokeDevice();
  const register = useRegisterSharedDevice();
  const [name, setName] = useState('');
  const [confirm, setConfirm] = useState(false);
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Aparelhos compartilhados' }} />
      <Card>
        <Text variant="subtitle">Usar este aparelho como compartilhado</Text>
        <Text muted>Depois de registrado, você sai da conta e o aparelho passa a mostrar a lista de funcionários com PIN.</Text>
        <TextField label="Nome do aparelho" value={name} onChangeText={setName} placeholder="Ex.: Tablet cozinha" />
        {register.error ? <Banner tone="danger" text={errorMessage(register.error)} /> : null}
        <Button title="Registrar este aparelho" disabled={name.trim().length < 2} onPress={() => setConfirm(true)} />
      </Card>
      <Section title="Registrados">
        {devices.isLoading ? <Skeleton lines={2} /> : null}
        {devices.data?.map((d) => (
          <Card key={d.id} style={d.revokedAt ? { opacity: 0.5 } : undefined}>
            <Row style={{ justifyContent: 'space-between' }}>
              <View style={{ flex: 1, gap: space.xs }}>
                <Text style={{ fontWeight: '700' }}>{d.name}</Text>
                <Text variant="small" muted>
                  {d.platform} · por {d.registeredBy.displayName} em {formatDateTime(d.registeredAt, timeZone)}
                  {d.revokedAt ? ' · revogado' : ''}
                </Text>
              </View>
              {!d.revokedAt ? <Button title="Revogar" variant="ghost" loading={revoke.isPending} onPress={() => revoke.mutate(d.id)} /> : null}
            </Row>
          </Card>
        ))}
      </Section>
      <ConfirmDialog
        visible={confirm}
        title="Registrar este aparelho?"
        lines={[{ label: 'Nome', value: name.trim() }]}
        description="Você sairá da sua conta neste aparelho."
        confirmLabel="Registrar e sair"
        loading={register.isPending}
        onCancel={() => setConfirm(false)}
        onConfirm={() => register.mutate(name.trim(), { onSuccess: () => router.replace('/select-employee'), onSettled: () => setConfirm(false) })}
      />
    </Screen>
  );
}
