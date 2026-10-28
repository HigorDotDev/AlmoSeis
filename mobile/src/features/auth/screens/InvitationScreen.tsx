import { zodResolver } from '@hookform/resolvers/zod';
import { router, useLocalSearchParams } from 'expo-router';
import { useEffect, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';

import { errorMessage, isApiError } from '@/api/errors';
import { FormTextField } from '@/components/form';
import { Banner, Button, Card, SegmentedControl, Screen, Skeleton, Text, TextField } from '@/components/ui';
import { maskBR, normalizeLogin, toE164 } from '@/lib/phone';
import { roleNames } from '@/lib/strings';

import { useAcceptInvitation, useInvitationPreview } from '../hooks';

const loose = { fullName: z.string(), phone: z.string(), email: z.string(), password: z.string(), login: z.string(), existingPassword: z.string() };
const schemas = {
  new: z.object({
    ...loose,
    displayName: z.string().trim().min(2, 'Como você quer ser chamado.'),
    fullName: z.string().trim().min(3, 'Informe seu nome.'),
    phone: z.string().refine((v) => toE164(v) !== null, 'Telefone com DDD.'),
    email: z.union([z.literal(''), z.email('E-mail inválido.')]),
    password: z.string().min(8, 'Pelo menos 8 caracteres.'),
  }),
  existing: z.object({
    ...loose,
    displayName: z.string().trim().min(2, 'Como você quer ser chamado.'),
    login: z.string().trim().min(3, 'Informe o telefone ou e-mail.'),
    existingPassword: z.string().min(1, 'Informe a senha.'),
  }),
};
type Form = z.infer<typeof schemas.new>;

/** 08 §8.7 `(auth)/invitation/[code]`: confere o estabelecimento e os perfis, depois "Sou novo" / "Já tenho conta". */
export default function InvitationScreen() {
  const params = useLocalSearchParams<{ code: string }>();
  const [code, setCode] = useState(params.code && params.code !== 'codigo' ? params.code.toUpperCase() : '');
  const [typed, setTyped] = useState(code);
  const preview = useInvitationPreview(code || undefined);
  const accept = useAcceptInvitation(code);
  const [mode, setMode] = useState<'new' | 'existing'>('new');
  const modeRef = useRef(mode);
  useEffect(() => {
    modeRef.current = mode;
  }, [mode]);
  const { control, handleSubmit } = useForm<Form>({
    resolver: (v, c, o) => zodResolver(schemas[modeRef.current])(v, c, o),
    defaultValues: { displayName: '', fullName: '', phone: '', email: '', password: '', login: '', existingPassword: '' },
  });

  const submit = handleSubmit((v) =>
    accept.mutate(
      {
        displayName: v.displayName.trim(),
        newAccount: mode === 'new' ? { fullName: v.fullName.trim(), email: v.email || null, phoneNumber: toE164(v.phone)!, password: v.password } : null,
        existingAccount: mode === 'existing' ? { login: normalizeLogin(v.login), password: v.existingPassword } : null,
      },
      { onSuccess: () => router.replace('/') },
    ),
  );

  return (
    <Screen style={{ paddingTop: 48 }}>
      <Text variant="title">Convite</Text>
      {!code || preview.isError ? (
        <>
          {preview.isError ? <Banner tone="danger" text="Código inválido, expirado ou já usado." /> : null}
          <TextField label="Código do convite" value={typed} onChangeText={(t) => setTyped(t.toUpperCase())} autoCapitalize="characters" maxLength={12} />
          <Button title="Conferir" onPress={() => setCode(typed.trim())} disabled={typed.trim().length < 4} />
        </>
      ) : preview.isLoading ? (
        <Skeleton lines={2} />
      ) : preview.data ? (
        <>
          <Card>
            <Text variant="subtitle">{preview.data.tenantName}</Text>
            <Text muted>
              Convite de {preview.data.invitedBy} · {preview.data.roles.map((r) => roleNames[r]).join(', ')}
            </Text>
          </Card>
          <SegmentedControl
            value={mode}
            onChange={setMode}
            options={[
              { value: 'new', label: 'Sou novo' },
              { value: 'existing', label: 'Já tenho conta' },
            ]}
          />
          <FormTextField control={control} name="displayName" label="Nome que aparece no app" autoCapitalize="words" />
          {mode === 'new' ? (
            <>
              <FormTextField control={control} name="fullName" label="Nome completo" autoCapitalize="words" />
              <FormTextField control={control} name="phone" label="Celular com DDD" keyboardType="phone-pad" format={maskBR} />
              <FormTextField control={control} name="email" label="E-mail (opcional)" keyboardType="email-address" autoCapitalize="none" />
              <FormTextField control={control} name="password" label="Senha" secureTextEntry />
            </>
          ) : (
            <>
              <FormTextField control={control} name="login" label="Telefone ou e-mail" autoCapitalize="none" />
              <FormTextField control={control} name="existingPassword" label="Senha" secureTextEntry />
            </>
          )}
          {accept.error ? (
            <Banner
              tone="danger"
              text={errorMessage(accept.error)}
              action={isApiError(accept.error, 'EmailAlreadyRegistered') || isApiError(accept.error, 'PhoneAlreadyRegistered') ? { label: 'Entrar com essa conta', onPress: () => setMode('existing') } : undefined}
            />
          ) : null}
          <Button title="Aceitar convite" onPress={submit} loading={accept.isPending} />
        </>
      ) : null}
      <Button title="Voltar" variant="ghost" onPress={() => (router.canGoBack() ? router.back() : router.replace('/login'))} />
    </Screen>
  );
}
