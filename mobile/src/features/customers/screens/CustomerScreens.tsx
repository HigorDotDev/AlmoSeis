/** 08 §8.10: `menu/customers` e `menu/customers/[id]` (endereços, fiado, anonimização ADR-0024). */
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { Switch, View } from 'react-native';

import { errorMessage } from '@/api/errors';
import type { Customer, CustomerAddressInput } from '@/api/types';
import {
  Banner,
  Button,
  Card,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  IconButton,
  KeyValue,
  Row,
  Screen,
  Section,
  Skeleton,
  Text,
  TextField,
} from '@/components/ui';
import { formatBusinessDate } from '@/lib/dates';
import { formatBRL, toCents } from '@/lib/money';
import { formatPhone, maskBR, toE164 } from '@/lib/phone';
import { orderTypeNames } from '@/lib/strings';
import { useCanWrite } from '@/stores/session';

import { useAddressMutations, useAnonymizeCustomer, useCreateCustomer, useCustomer, useCustomerPending, useCustomerSearch, useUpdateCustomer } from '../hooks';

export function CustomersScreen() {
  const [search, setSearch] = useState('');
  const results = useCustomerSearch(search.trim());
  const canManage = useCanWrite('customers.manage');
  const [creating, setCreating] = useState(false);
  const items = results.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <Screen>
      <Stack.Screen options={{ title: 'Clientes' }} />
      <TextField label="Buscar por nome ou telefone" value={search} onChangeText={setSearch} />
      {canManage ? <Button title="Novo cliente" icon="add" variant="secondary" onPress={() => setCreating(true)} /> : null}
      {creating ? <NewCustomer onDone={() => setCreating(false)} /> : null}
      {results.isLoading ? <Skeleton /> : null}
      {!results.isLoading && items.length === 0 ? <EmptyState title="Nenhum cliente" /> : null}
      {items.map((c) => (
        <Card key={c.id} onPress={() => router.push(`/menu/customers/${c.id}`)}>
          <Row style={{ justifyContent: 'space-between' }}>
            <Text style={{ fontWeight: '700' }}>{c.name}</Text>
            {toCents(c.pendingAmount) > 0 ? <Text style={{ fontWeight: '700' }}>Deve {formatBRL(c.pendingAmount)}</Text> : null}
          </Row>
          <Text variant="small" muted>
            {formatPhone(c.phoneNumber)}
          </Text>
        </Card>
      ))}
      {results.hasNextPage ? <Button title="Carregar mais" variant="secondary" onPress={() => void results.fetchNextPage()} /> : null}
    </Screen>
  );
}

function NewCustomer({ onDone }: { onDone: () => void }) {
  const create = useCreateCustomer();
  const [name, setName] = useState('');
  const [phone, setPhone] = useState('');
  return (
    <Card>
      <TextField label="Nome" value={name} onChangeText={setName} autoCapitalize="words" />
      <TextField label="Telefone" value={phone} onChangeText={(t) => setPhone(maskBR(t))} keyboardType="phone-pad" />
      {create.error ? <Banner tone="danger" text={errorMessage(create.error)} /> : null}
      <Button
        title="Salvar cliente"
        disabled={name.trim().length < 2 || (phone !== '' && toE164(phone) === null)}
        loading={create.isPending}
        onPress={() =>
          create.mutate({ name: name.trim(), phoneNumber: phone ? toE164(phone) : null, notes: null, addresses: [] }, { onSuccess: (c) => { onDone(); router.push(`/menu/customers/${c.id}`); } })
        }
      />
    </Card>
  );
}

export function CustomerDetailScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const customer = useCustomer(id);
  if (customer.isLoading) return <Skeleton lines={4} />;
  if (!customer.data) return <ErrorState message={errorMessage(customer.error)} onRetry={() => void customer.refetch()} />;
  return <CustomerDetail customer={customer.data} />;
}

const emptyAddress = (): CustomerAddressInput => ({ label: null, street: '', number: null, complement: null, neighborhood: null, reference: null, isDefault: false });

function CustomerDetail({ customer: c }: { customer: Customer }) {
  const canManage = useCanWrite('customers.manage');
  const canAnonymize = useCanWrite('settings.manage');
  const pending = useCustomerPending(c.id);
  const update = useUpdateCustomer();
  const addresses = useAddressMutations();
  const anonymize = useAnonymizeCustomer();
  const [name, setName] = useState(c.name);
  const [phone, setPhone] = useState(formatPhone(c.phoneNumber));
  const [notes, setNotes] = useState(c.notes ?? '');
  const [address, setAddress] = useState<CustomerAddressInput | null>(null);
  const [anon, setAnon] = useState(false);
  const [typed, setTyped] = useState('');
  const [reason, setReason] = useState('');
  const anonymized = c.anonymizedAt !== null;
  const error = update.error ?? addresses.add.error ?? addresses.remove.error ?? addresses.update.error ?? anonymize.error;

  return (
    <Screen>
      <Stack.Screen options={{ title: c.name }} />
      {anonymized ? <Banner tone="info" text="Cliente anonimizado: os dados pessoais foram apagados (ADR-0024)." /> : null}
      {error ? <Banner tone="danger" text={errorMessage(error)} /> : null}

      <Section title="Fiado">
        {pending.data && pending.data.orders.length ? (
          <>
            <KeyValue label="Total pendente" value={formatBRL(pending.data.totalPending)} strong />
            {pending.data.orders.map((o) => (
              <Card key={o.id} onPress={() => router.push(`/orders/${o.id}/pay`)}>
                <Row style={{ justifyContent: 'space-between' }}>
                  <Text>
                    #{o.orderNumber} · {orderTypeNames[o.orderType]} · {formatBusinessDate(o.businessDate)}
                  </Text>
                  <Text style={{ fontWeight: '700' }}>{formatBRL(o.balance)}</Text>
                </Row>
                <Text variant="small" muted>
                  Receber
                </Text>
              </Card>
            ))}
          </>
        ) : (
          <Text muted>Nada pendente.</Text>
        )}
      </Section>

      {!anonymized ? (
        <>
          <Section title="Dados">
            <TextField label="Nome" value={name} onChangeText={setName} editable={canManage} />
            <TextField label="Telefone" value={phone} onChangeText={(t) => setPhone(maskBR(t))} keyboardType="phone-pad" editable={canManage} />
            <TextField label="Observações" value={notes} onChangeText={setNotes} multiline editable={canManage} />
            {canManage ? (
              <Button
                title="Salvar dados"
                variant="secondary"
                loading={update.isPending}
                onPress={() => update.mutate({ id: c.id, version: c.version, body: { name: name.trim(), phoneNumber: phone ? toE164(phone) : null, notes: notes.trim() || null } })}
              />
            ) : null}
          </Section>

          <Section title="Endereços" right={canManage ? <Button title="Novo" variant="ghost" onPress={() => setAddress(emptyAddress())} /> : undefined}>
            {c.addresses.map((a) => (
              <Card key={a.id}>
                <Row>
                  <View style={{ flex: 1 }}>
                    <Text style={{ fontWeight: '700' }}>
                      {a.label ?? 'Endereço'}
                      {a.isDefault ? ' · padrão' : ''}
                    </Text>
                    <Text variant="small">{[a.street, a.number, a.complement, a.neighborhood].filter(Boolean).join(', ')}</Text>
                    {a.reference ? <Text variant="small" muted>{a.reference}</Text> : null}
                  </View>
                  {canManage && !a.isDefault ? <IconButton icon="star-outline" label="Tornar padrão" onPress={() => addresses.update.mutate({ customer: c, addressId: a.id, body: { isDefault: true } })} /> : null}
                  {canManage ? <IconButton icon="trash-outline" label="Remover endereço" onPress={() => addresses.remove.mutate({ customer: c, addressId: a.id })} /> : null}
                </Row>
              </Card>
            ))}
            {address ? (
              <Card>
                <TextField label="Nome (Casa, Trabalho…)" value={address.label ?? ''} onChangeText={(label) => setAddress({ ...address, label: label || null })} />
                <TextField label="Rua" value={address.street} onChangeText={(street) => setAddress({ ...address, street })} />
                <TextField label="Número" value={address.number ?? ''} onChangeText={(number) => setAddress({ ...address, number: number || null })} />
                <TextField label="Complemento" value={address.complement ?? ''} onChangeText={(complement) => setAddress({ ...address, complement: complement || null })} />
                <TextField label="Bairro" value={address.neighborhood ?? ''} onChangeText={(neighborhood) => setAddress({ ...address, neighborhood: neighborhood || null })} />
                <TextField label="Referência" value={address.reference ?? ''} onChangeText={(reference) => setAddress({ ...address, reference: reference || null })} />
                <Row style={{ justifyContent: 'space-between' }}>
                  <Text>Padrão</Text>
                  <Switch value={address.isDefault} onValueChange={(isDefault) => setAddress({ ...address, isDefault })} />
                </Row>
                <Button
                  title="Salvar endereço"
                  disabled={address.street.trim() === ''}
                  loading={addresses.add.isPending}
                  onPress={() => addresses.add.mutate({ customer: c, body: address }, { onSuccess: () => setAddress(null) })}
                />
              </Card>
            ) : null}
          </Section>

          {canAnonymize ? <Button title="Anonimizar cliente" variant="danger" onPress={() => setAnon(true)} /> : null}
        </>
      ) : null}

      <ConfirmDialog
        visible={anon}
        title="Anonimizar cliente?"
        description={`Nome, telefone, endereços e observações serão apagados de forma permanente. Os pedidos continuam, sem os dados pessoais. Para confirmar, digite o nome: ${c.name}`}
        confirmLabel="Anonimizar"
        danger
        loading={anonymize.isPending}
        onCancel={() => setAnon(false)}
        onConfirm={() => {
          if (typed.trim() !== c.name.trim() || reason.trim().length < 5) return;
          anonymize.mutate({ customer: c, reason: reason.trim() }, { onSuccess: () => setAnon(false) });
        }}
      >
        <TextField label="Digite o nome do cliente" value={typed} onChangeText={setTyped} />
        <TextField label="Motivo" value={reason} onChangeText={setReason} placeholder="Ex.: pedido do titular via WhatsApp" />
      </ConfirmDialog>
    </Screen>
  );
}
