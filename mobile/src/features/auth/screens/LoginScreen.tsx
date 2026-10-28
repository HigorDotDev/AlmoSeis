import { zodResolver } from '@hookform/resolvers/zod';
import { router } from 'expo-router';
import { useForm } from 'react-hook-form';
import { z } from 'zod';

import { errorMessage } from '@/api/errors';
import { FormTextField } from '@/components/form';
import { Banner, Button, Screen, Text } from '@/components/ui';
import { normalizeLogin } from '@/lib/phone';
import { useSession } from '@/stores/session';

import { useLogin } from '../hooks';

const schema = z.object({
  login: z.string().trim().min(3, 'Informe o telefone ou e-mail.'),
  password: z.string().min(1, 'Informe a senha.'),
});

const reasons: Record<string, string> = {
  expired: 'Sua sessão expirou. Entre de novo.',
  revoked: 'Seu acesso foi alterado ou revogado. Entre de novo.',
};

/** 08 §8.7 `(auth)/login`. */
export default function LoginScreen() {
  const login = useLogin();
  const reason = useSession((s) => s.signOutReason);
  const { control, handleSubmit } = useForm({ resolver: zodResolver(schema), defaultValues: { login: '', password: '' } });

  const submit = handleSubmit((v) =>
    login.mutate({ login: normalizeLogin(v.login), password: v.password }, { onSuccess: () => router.replace('/') }),
  );

  return (
    <Screen style={{ paddingTop: 64 }}>
      <Text variant="big">AlmoSeis</Text>
      <Text muted>Pedidos, cozinha e caixa do seu restaurante.</Text>
      {reason && reasons[reason] ? <Banner tone="info" text={reasons[reason]!} /> : null}
      <FormTextField control={control} name="login" label="Telefone ou e-mail" autoCapitalize="none" keyboardType="email-address" autoComplete="username" />
      <FormTextField control={control} name="password" label="Senha" secureTextEntry autoComplete="current-password" onSubmitEditing={() => void submit()} />
      {login.error ? <Banner tone="danger" text={errorMessage(login.error)} /> : null}
      <Button title="Entrar" onPress={submit} loading={login.isPending} />
      <Button title="Esqueci a senha" variant="ghost" onPress={() => router.push('/forgot-password')} />
      <Button title="Criar conta do estabelecimento" variant="secondary" onPress={() => router.push('/signup')} />
      <Button title="Tenho um código de convite" variant="secondary" onPress={() => router.push('/invitation/codigo')} />
    </Screen>
  );
}
