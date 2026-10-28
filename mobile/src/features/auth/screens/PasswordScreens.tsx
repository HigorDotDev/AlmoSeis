import { zodResolver } from '@hookform/resolvers/zod';
import { router, useLocalSearchParams } from 'expo-router';
import { useForm } from 'react-hook-form';
import { z } from 'zod';

import { errorMessage } from '@/api/errors';
import { FormTextField } from '@/components/form';
import { Banner, Button, Screen, Text } from '@/components/ui';
import { normalizeLogin } from '@/lib/phone';

import { useForgotPassword, useResetPassword } from '../hooks';

/** 08 §8.7 `(auth)/forgot-password`: sempre a mesma resposta, exista ou não a conta (06 §6.3). */
export function ForgotPasswordScreen() {
  const forgot = useForgotPassword();
  const { control, handleSubmit } = useForm({
    resolver: zodResolver(z.object({ email: z.email('E-mail inválido.') })),
    defaultValues: { email: '' },
  });
  return (
    <Screen style={{ paddingTop: 48 }}>
      <Text variant="title">Esqueci a senha</Text>
      <Text muted>Informe o e-mail da sua conta. Sem e-mail cadastrado, peça ao proprietário um código de redefinição.</Text>
      <FormTextField control={control} name="email" label="E-mail" keyboardType="email-address" autoCapitalize="none" />
      {forgot.isSuccess ? <Banner tone="success" text="Se existir uma conta com esse e-mail, enviamos um link. Ele vale por 30 minutos." /> : null}
      {forgot.error ? <Banner tone="danger" text={errorMessage(forgot.error)} /> : null}
      <Button title="Enviar link" onPress={handleSubmit((v) => forgot.mutate(v.email.trim()))} loading={forgot.isPending} />
      <Button title="Tenho um código do proprietário" variant="secondary" onPress={() => router.push('/reset-password')} />
      <Button title="Voltar" variant="ghost" onPress={() => router.back()} />
    </Screen>
  );
}

const resetSchema = z.object({
  code: z.string(),
  login: z.string(),
  newPassword: z.string().min(8, 'Pelo menos 8 caracteres.'),
});

/** Código do proprietário + login, ou o link do e-mail (`emailToken`). */
export function ResetPasswordScreen() {
  const { emailToken } = useLocalSearchParams<{ emailToken?: string }>();
  const reset = useResetPassword();
  const { control, handleSubmit, setError } = useForm({ resolver: zodResolver(resetSchema), defaultValues: { code: '', login: '', newPassword: '' } });

  const submit = handleSubmit((v) => {
    if (!emailToken) {
      if (v.code.trim().length < 4) return setError('code', { message: 'Informe o código.' });
      if (v.login.trim().length < 3) return setError('login', { message: 'Informe o telefone ou e-mail.' });
    }
    reset.mutate(
      emailToken
        ? { emailToken, newPassword: v.newPassword }
        : { code: v.code.trim().toUpperCase(), login: normalizeLogin(v.login), newPassword: v.newPassword },
    );
  });

  if (reset.isSuccess) {
    return (
      <Screen style={{ paddingTop: 48 }}>
        <Banner tone="success" text="Senha alterada. Entre com a nova senha." />
        <Button title="Ir para o login" onPress={() => router.replace('/login')} />
      </Screen>
    );
  }

  return (
    <Screen style={{ paddingTop: 48 }}>
      <Text variant="title">Nova senha</Text>
      {emailToken ? null : (
        <>
          <FormTextField control={control} name="code" label="Código recebido do proprietário" autoCapitalize="characters" />
          <FormTextField control={control} name="login" label="Seu telefone ou e-mail" autoCapitalize="none" />
        </>
      )}
      <FormTextField control={control} name="newPassword" label="Nova senha" secureTextEntry />
      {reset.error ? <Banner tone="danger" text={errorMessage(reset.error)} /> : null}
      <Button title="Salvar nova senha" onPress={submit} loading={reset.isPending} />
      <Button title="Voltar" variant="ghost" onPress={() => router.back()} />
    </Screen>
  );
}
