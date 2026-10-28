/** Entrada, cadastro, convite, senha e aparelho compartilhado (06 §6.3, 08 §8.7). */
import { useMutation, useQuery } from '@tanstack/react-query';

import { authApi } from '@/api/endpoints/auth';
import { devicesApi, invitationsApi } from '@/api/endpoints/tenant';
import type { ExistingAccount, NewAccount, Session, SignupBody } from '@/api/types';
import { APP_PLATFORM } from '@/config';
import { secure } from '@/stores/secure';
import { useSession } from '@/stores/session';

export function useLogin() {
  const signIn = useSession((s) => s.signIn);
  return useMutation({
    mutationFn: async ({ login, password }: { login: string; password: string }) => {
      const installationId = await secure.installationId();
      const preferredTenantId = await secure.lastTenantId();
      return (await authApi.login({ login, password, installationId, preferredTenantId })).data;
    },
    onSuccess: (s: Session) => signIn(s),
  });
}

export function useSignup() {
  const signIn = useSession((s) => s.signIn);
  return useMutation({
    mutationFn: async (body: Omit<SignupBody, 'installationId'>) => {
      const installationId = await secure.installationId();
      return (await authApi.signup({ ...body, installationId })).data;
    },
    onSuccess: (s: Session) => signIn(s),
  });
}

export function useInvitationPreview(code: string | undefined) {
  return useQuery({
    queryKey: ['invitation', code],
    queryFn: async () => (await invitationsApi.preview(code!)).data,
    enabled: !!code && code.length >= 4,
    retry: false,
  });
}

export function useAcceptInvitation(code: string) {
  const signIn = useSession((s) => s.signIn);
  return useMutation({
    mutationFn: async (body: { newAccount: NewAccount | null; existingAccount: ExistingAccount | null; displayName: string }) => {
      const installationId = await secure.installationId();
      return (await invitationsApi.accept(code, { ...body, installationId })).data;
    },
    onSuccess: (s: Session) => signIn(s),
  });
}

export function useForgotPassword() {
  return useMutation({ mutationFn: (email: string) => authApi.forgotPassword(email) });
}

export function useResetPassword() {
  return useMutation({
    mutationFn: (body: { code: string; login: string; newPassword: string } | { emailToken: string; newPassword: string }) => authApi.resetPassword(body),
  });
}

/**
 * Registra este aparelho como compartilhado (ADR-0027): guarda o `deviceSecret` e desloga quem registrou,
 * deixando o aparelho na seleção de funcionário.
 */
export function useRegisterSharedDevice() {
  const session = useSession();
  return useMutation({
    mutationFn: async (name: string) => {
      const installationId = await secure.installationId();
      return (await devicesApi.register({ installationId, name, platform: APP_PLATFORM })).data;
    },
    onSuccess: async (device) => {
      await session.setSharedDevice({
        id: device.id,
        tenantId: device.tenantId,
        tenantName: session.me?.tenant.name ?? '',
        name: device.name,
        installationId: device.installationId,
        deviceSecret: device.deviceSecret,
      });
      await session.signOut('deviceRegistered');
    },
  });
}

export function useDeviceEmployees() {
  const device = useSession((s) => s.sharedDevice);
  return useQuery({
    queryKey: ['deviceEmployees', device?.id],
    queryFn: async () => (await authApi.deviceEmployees(device!.installationId, device!.tenantId, device!.deviceSecret)).data,
    enabled: !!device,
    staleTime: 60_000,
  });
}

export function usePinLogin() {
  const device = useSession((s) => s.sharedDevice);
  const signIn = useSession((s) => s.signIn);
  return useMutation({
    mutationFn: async ({ employeeId, pin }: { employeeId: string; pin: string }) =>
      (await authApi.pinLogin({ tenantId: device!.tenantId, installationId: device!.installationId, employeeId, pin }, device!.deviceSecret)).data,
    onSuccess: (s: Session) => signIn(s),
  });
}
