import { zodResolver } from '@hookform/resolvers/zod';
import { router } from 'expo-router';
import { useEffect, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { Switch } from 'react-native';
import { z } from 'zod';

import { errorMessage, isApiError } from '@/api/errors';
import { FormTextField, serverFieldError } from '@/components/form';
import { Banner, Button, Row, Screen, SegmentedControl, Text } from '@/components/ui';
import { TERMS_VERSION } from '@/config';
import { maskBR, maskDocument, normalizeLogin, toE164 } from '@/lib/phone';

import { useSignup } from '../hooks';

const accountNew = {
  fullName: z.string().trim().min(3, 'Informe seu nome.'),
  phone: z.string().refine((v) => toE164(v) !== null, 'Telefone com DDD.'),
  email: z.union([z.literal(''), z.email('E-mail inválido.')]),
  password: z.string().min(8, 'Pelo menos 8 caracteres.'),
};
const accountExisting = {
  existingLogin: z.string().trim().min(3, 'Informe o telefone ou e-mail.'),
  existingPassword: z.string().min(1, 'Informe a senha.'),
};
const tenantFields = {
  tenantName: z.string().trim().min(2, 'Informe o nome do estabelecimento.'),
  document: z.string().refine((v) => [11, 14].includes(v.replace(/\D/g, '').length), 'CPF (11) ou CNPJ (14 dígitos).'),
  city: z.string().trim().min(2, 'Informe a cidade.'),
  state: z.string().trim().length(2, 'UF com 2 letras.'),
};
const loose = { fullName: z.string(), phone: z.string(), email: z.string(), password: z.string(), existingLogin: z.string(), existingPassword: z.string() };
/** Só valida a conta do modo escolhido ("Sou novo" ou "Já tenho conta"). */
const schemas = {
  new: z.object({ ...loose, ...accountNew, ...tenantFields }),
  existing: z.object({ ...loose, ...accountExisting, ...tenantFields }),
};

type Form = z.infer<typeof schemas.new>;

const steps = ['Você', 'Estabelecimento', 'Termos'] as const;

/** 08 §8.7 `(auth)/signup`. Nenhum preço aparece aqui (ADR-0017). */
export default function SignupScreen() {
  const signup = useSignup();
  const [step, setStep] = useState(0);
  const [accepted, setAccepted] = useState(false);
  const [mode, setMode] = useState<'new' | 'existing'>('new');
  const modeRef = useRef(mode);
  useEffect(() => {
    modeRef.current = mode;
  }, [mode]);
  const { control, handleSubmit, trigger, setValue, getValues } = useForm<Form>({
    resolver: (values, context, options) => zodResolver(schemas[modeRef.current])(values, context, options),
    defaultValues: { fullName: '', phone: '', email: '', password: '', tenantName: '', document: '', city: '', state: '', existingLogin: '', existingPassword: '' },
    mode: 'onTouched',
  });

  const next = async () => {
    const fields: (keyof Form)[][] = [mode === 'new' ? ['fullName', 'phone', 'email', 'password'] : ['existingLogin', 'existingPassword'], ['tenantName', 'document', 'city', 'state']];
    if (await trigger(fields[step])) setStep(step + 1);
  };

  const submit = handleSubmit((v) => {
    const digits = v.document.replace(/\D/g, '');
    signup.mutate(
      {
        newAccount: mode === 'new' ? { fullName: v.fullName.trim(), email: v.email || null, phoneNumber: toE164(v.phone)!, password: v.password } : null,
        existingAccount: mode === 'existing' ? { login: normalizeLogin(v.existingLogin), password: v.existingPassword } : null,
        tenant: {
          name: v.tenantName.trim(),
          documentType: digits.length === 11 ? 'CPF' : 'CNPJ',
          documentNumber: digits,
          city: v.city.trim(),
          state: v.state.trim().toUpperCase(),
          timeZone: 'America/Sao_Paulo',
        },
        acceptedTermsVersion: TERMS_VERSION,
      },
      { onSuccess: () => router.replace('/') },
    );
  });

  const err = signup.error;
  const alreadyRegistered = isApiError(err, 'EmailAlreadyRegistered') || isApiError(err, 'PhoneAlreadyRegistered');

  return (
    <Screen style={{ paddingTop: 48 }}>
      <Text variant="title">Criar conta</Text>
      <Text muted>
        Passo {step + 1} de 3 · {steps[step]}
      </Text>

      {step === 0 ? (
        <>
          <SegmentedControl
            value={mode}
            onChange={setMode}
            options={[
              { value: 'new', label: 'Sou novo' },
              { value: 'existing', label: 'Já tenho conta' },
            ]}
          />
          {mode === 'new' ? (
            <>
              <FormTextField control={control} name="fullName" label="Seu nome" autoCapitalize="words" />
              <FormTextField control={control} name="phone" label="Celular com DDD" keyboardType="phone-pad" format={maskBR} serverError={serverFieldError(err, 'newAccount.phoneNumber')} />
              <FormTextField control={control} name="email" label="E-mail (opcional, para recuperar a senha)" keyboardType="email-address" autoCapitalize="none" />
              <FormTextField control={control} name="password" label="Senha" secureTextEntry serverError={serverFieldError(err, 'newAccount.password')} />
            </>
          ) : (
            <>
              <FormTextField control={control} name="existingLogin" label="Telefone ou e-mail da sua conta" autoCapitalize="none" />
              <FormTextField control={control} name="existingPassword" label="Senha" secureTextEntry />
            </>
          )}
        </>
      ) : null}

      {step === 1 ? (
        <>
          <FormTextField control={control} name="tenantName" label="Nome do estabelecimento" autoCapitalize="words" />
          <FormTextField control={control} name="document" label="CPF ou CNPJ" keyboardType="number-pad" format={maskDocument} serverError={serverFieldError(err, 'tenant.documentNumber')} />
          <FormTextField control={control} name="city" label="Cidade" autoCapitalize="words" />
          <FormTextField control={control} name="state" label="UF" autoCapitalize="characters" maxLength={2} />
        </>
      ) : null}

      {step === 2 ? (
        <>
          <Text>Você terá 7 dias para usar o AlmoSeis com todas as funções. Ao continuar, você aceita os Termos de Uso e a Política de Privacidade.</Text>
          <Row style={{ justifyContent: 'space-between' }}>
            <Text style={{ flex: 1 }}>Li e aceito os termos</Text>
            <Switch value={accepted} onValueChange={setAccepted} accessibilityLabel="Aceito os termos" />
          </Row>
        </>
      ) : null}

      {err ? (
        <Banner
          tone="danger"
          text={errorMessage(err)}
          action={
            alreadyRegistered
              ? {
                  label: 'Entrar com essa conta',
                  onPress: () => {
                    const v = getValues();
                    setValue('existingLogin', v.email || v.phone);
                    setMode('existing');
                    setStep(0);
                    signup.reset();
                  },
                }
              : undefined
          }
        />
      ) : null}

      {step < 2 ? <Button title="Continuar" onPress={() => void next()} /> : <Button title="Criar estabelecimento" onPress={submit} disabled={!accepted} loading={signup.isPending} />}
      <Button title={step === 0 ? 'Voltar para o login' : 'Voltar'} variant="ghost" onPress={() => (step === 0 ? router.back() : setStep(step - 1))} />
    </Screen>
  );
}
