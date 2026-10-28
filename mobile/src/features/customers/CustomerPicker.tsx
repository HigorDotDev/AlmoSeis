/** Busca por telefone ou nome, com as pendências do cliente ("Deve R$ 35,00"), ou nome e telefone livres (08 §8.9). */
import { useEffect, useState } from 'react';
import { Pressable, Switch, View } from 'react-native';

import type { Customer } from '@/api/types';
import { Button, Card, Row, Text, TextField, space, useColors } from '@/components/ui';
import { formatBRL, toCents } from '@/lib/money';
import { formatPhone, maskBR } from '@/lib/phone';
import { usePermission } from '@/stores/session';

import { useCustomerSearch } from './hooks';

export interface PickedCustomer {
  id: string | null;
  name: string;
  phone: string;
  saveCustomer: boolean;
}

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

export function CustomerPicker({
  value,
  onChange,
  onSelectCustomer,
  errors,
}: {
  value: PickedCustomer;
  onChange: (v: PickedCustomer) => void;
  /** Cliente cadastrado escolhido: a tela usa os endereços dele. */
  onSelectCustomer: (c: Customer | null) => void;
  errors?: { name?: string; phone?: string };
}) {
  const c = useColors();
  const canSave = usePermission('customers.manage');
  const [term, setTerm] = useState('');
  const debounced = useDebounced(term.trim(), 350);
  const results = useCustomerSearch(debounced, debounced.length >= 2 && value.id === null);
  const items = results.data?.pages.flatMap((p) => p.items).slice(0, 6) ?? [];

  if (value.id) {
    return (
      <Card>
        <Row style={{ justifyContent: 'space-between' }}>
          <View style={{ flex: 1 }}>
            <Text variant="subtitle">{value.name}</Text>
            <Text muted>{maskBR(value.phone)}</Text>
          </View>
          <Button
            title="Trocar"
            variant="ghost"
            onPress={() => {
              onChange({ id: null, name: '', phone: '', saveCustomer: false });
              onSelectCustomer(null);
            }}
          />
        </Row>
      </Card>
    );
  }

  return (
    <View style={{ gap: space.sm }}>
      <TextField label="Buscar cliente (telefone ou nome)" value={term} onChangeText={setTerm} placeholder="Ex.: 99999 ou João" />
      {items.map((cust) => (
        <Pressable
          key={cust.id}
          accessibilityRole="button"
          onPress={() => {
            onChange({ id: cust.id, name: cust.name, phone: cust.phoneNumber ?? '', saveCustomer: false });
            onSelectCustomer(cust);
            setTerm('');
          }}
          style={({ pressed }) => ({ padding: space.md, borderRadius: 8, backgroundColor: c.surface, opacity: pressed ? 0.6 : 1, borderWidth: 1, borderColor: c.border })}
        >
          <Text>{cust.name}</Text>
          <Text variant="small" muted>
            {formatPhone(cust.phoneNumber)}
            {toCents(cust.pendingAmount) > 0 ? ` · Deve ${formatBRL(cust.pendingAmount)}` : ''}
          </Text>
        </Pressable>
      ))}
      <Text variant="small" muted>
        Ou informe sem cadastro:
      </Text>
      <TextField label="Nome" value={value.name} onChangeText={(name) => onChange({ ...value, name })} error={errors?.name} autoCapitalize="words" />
      <TextField label="Telefone (opcional)" value={value.phone} onChangeText={(t) => onChange({ ...value, phone: maskBR(t) })} keyboardType="phone-pad" error={errors?.phone} />
      {canSave ? (
        <Row style={{ justifyContent: 'space-between' }}>
          <Text style={{ flex: 1 }}>Salvar cliente</Text>
          <Switch value={value.saveCustomer} onValueChange={(saveCustomer) => onChange({ ...value, saveCustomer })} accessibilityLabel="Salvar cliente" />
        </Row>
      ) : null}
    </View>
  );
}
