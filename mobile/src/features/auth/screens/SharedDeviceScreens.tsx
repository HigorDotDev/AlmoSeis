import { Ionicons } from '@expo/vector-icons';
import { router, useLocalSearchParams } from 'expo-router';
import { useState } from 'react';
import { Pressable, View } from 'react-native';

import { errorMessage, isApiError } from '@/api/errors';
import { Banner, Button, EmptyState, ErrorState, Screen, Skeleton, Text, radius, space, useColors } from '@/components/ui';
import { roleNames } from '@/lib/strings';
import { useSession } from '@/stores/session';

import { useDeviceEmployees, usePinLogin } from '../hooks';

/** 08 §8.7 `(shared-device)/select-employee`: grade de nomes grandes. */
export function SelectEmployeeScreen() {
  const c = useColors();
  const device = useSession((s) => s.sharedDevice);
  const setSharedDevice = useSession((s) => s.setSharedDevice);
  const employees = useDeviceEmployees();

  if (isApiError(employees.error, 'InvalidDeviceSecret')) {
    return (
      <Screen style={{ paddingTop: 64 }}>
        <EmptyState icon="tablet-landscape-outline" title="Aparelho revogado" text="Este aparelho não está mais registrado como compartilhado." />
        <Button
          title="Entrar com login e senha"
          onPress={() => {
            void setSharedDevice(null);
            router.replace('/login');
          }}
        />
      </Screen>
    );
  }

  return (
    <Screen style={{ paddingTop: 48 }}>
      <Text variant="title">{employees.data?.tenant.name ?? device?.tenantName}</Text>
      <Text muted>{device?.name} · Quem vai usar?</Text>
      {employees.isLoading ? <Skeleton lines={4} /> : null}
      {employees.isError ? <ErrorState message={errorMessage(employees.error)} onRetry={() => void employees.refetch()} /> : null}
      {employees.data?.employees.length === 0 ? (
        <EmptyState title="Ninguém com PIN" text="Cada funcionário define o PIN no próprio perfil antes de usar o aparelho compartilhado." />
      ) : null}
      <View style={{ flexDirection: 'row', flexWrap: 'wrap', gap: space.md }}>
        {employees.data?.employees.map((e) => (
          <Pressable
            key={e.id}
            accessibilityRole="button"
            accessibilityLabel={`Entrar como ${e.displayName}`}
            onPress={() => router.push({ pathname: '/pin', params: { employeeId: e.id, name: e.displayName } })}
            style={({ pressed }) => ({
              width: '47%',
              minHeight: 110,
              borderRadius: radius.lg,
              backgroundColor: c.surface,
              borderWidth: 1,
              borderColor: c.border,
              alignItems: 'center',
              justifyContent: 'center',
              gap: space.xs,
              opacity: pressed ? 0.7 : 1,
            })}
          >
            <Ionicons name="person-circle-outline" size={36} color={c.primary} />
            <Text variant="subtitle">{e.displayName}</Text>
            <Text variant="small" muted>
              {e.roles.map((r) => roleNames[r]).join(', ')}
            </Text>
          </Pressable>
        ))}
      </View>
      <Button title="Entrar com login e senha" variant="ghost" onPress={() => router.push('/login')} />
    </Screen>
  );
}

const KEYS = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '', '0', '⌫'];

/** Teclado próprio, sem o do sistema (08 §8.7). */
export function PinScreen() {
  const c = useColors();
  const { employeeId, name } = useLocalSearchParams<{ employeeId: string; name: string }>();
  const [pin, setPin] = useState('');
  const login = usePinLogin();

  const press = (k: string) => {
    if (login.isPending) return;
    if (k === '⌫') return setPin((p) => p.slice(0, -1));
    if (k === '' || pin.length >= 6) return;
    setPin((p) => p + k);
  };

  const submit = () =>
    login.mutate(
      { employeeId, pin },
      {
        onSuccess: () => router.replace('/'),
        onError: () => setPin(''),
      },
    );

  return (
    <Screen style={{ paddingTop: 48, alignItems: 'center' }}>
      <Text variant="title">Olá, {name}</Text>
      <Text muted>Digite seu PIN</Text>
      <View style={{ flexDirection: 'row', gap: space.md, marginVertical: space.lg }} accessibilityLabel={`${pin.length} dígitos digitados`}>
        {Array.from({ length: 6 }, (_, i) => (
          <View key={i} style={{ width: 16, height: 16, borderRadius: 8, backgroundColor: i < pin.length ? c.primary : c.border }} />
        ))}
      </View>
      {login.error ? <Banner tone="danger" text={errorMessage(login.error)} /> : null}
      <View style={{ flexDirection: 'row', flexWrap: 'wrap', width: 300, gap: space.md, justifyContent: 'center' }}>
        {KEYS.map((k, i) => (
          <Pressable
            key={i}
            onPress={() => press(k)}
            disabled={k === ''}
            accessibilityRole="button"
            accessibilityLabel={k === '⌫' ? 'Apagar' : k}
            style={({ pressed }) => ({
              width: 84,
              height: 72,
              borderRadius: radius.md,
              backgroundColor: k === '' ? 'transparent' : c.surface,
              alignItems: 'center',
              justifyContent: 'center',
              opacity: pressed ? 0.6 : 1,
            })}
          >
            <Text variant="big">{k}</Text>
          </Pressable>
        ))}
      </View>
      <Button title="Entrar" onPress={submit} disabled={pin.length < 4} loading={login.isPending} style={{ alignSelf: 'stretch' }} />
      <Button title="Trocar pessoa" variant="ghost" onPress={() => router.back()} />
    </Screen>
  );
}
