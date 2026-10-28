/** Funcionários, convites e aparelhos compartilhados (06 §6.3/6.4). */
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { auditApi } from '@/api/endpoints/sync';
import { devicesApi, employeesApi } from '@/api/endpoints/tenant';
import { qk } from '@/api/queryKeys';
import type { Employee, Role } from '@/api/types';

export function useEmployees(status?: 'ACTIVE' | 'INACTIVE') {
  return useQuery({ queryKey: qk.employees(status), queryFn: async () => (await employeesApi.list(status)).data.items });
}

export function useEmployee(id: string | undefined) {
  return useQuery({ queryKey: qk.employee(id ?? ''), queryFn: async () => (await employeesApi.get(id!)).data, enabled: !!id });
}

export function useInvitations() {
  return useQuery({ queryKey: qk.invitations, queryFn: async () => (await employeesApi.invitations('PENDING')).data.items });
}

export function useEmployeeMutations() {
  const client = useQueryClient();
  const onSuccess = (e: Employee) => {
    client.setQueryData(qk.employee(e.id), e);
    void client.invalidateQueries({ queryKey: ['employees'] });
  };
  return {
    update: useMutation({
      mutationFn: async ({ employee, body }: { employee: Employee; body: { displayName?: string; roles?: Role[] } }) =>
        (await employeesApi.update(employee.id, body, employee.version)).data,
      onSuccess,
    }),
    deactivate: useMutation({ mutationFn: async (e: Employee) => (await employeesApi.deactivate(e.id, e.version)).data, onSuccess }),
    reactivate: useMutation({ mutationFn: async (e: Employee) => (await employeesApi.reactivate(e.id, e.version)).data, onSuccess }),
    resetCode: useMutation({ mutationFn: async (e: Employee) => (await employeesApi.passwordResetCode(e.id)).data }),
  };
}

export function useInvite() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (roles: Role[]) => (await employeesApi.invite(roles)).data,
    onSuccess: () => client.invalidateQueries({ queryKey: qk.invitations }),
  });
}

export function useRevokeInvitation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => employeesApi.revokeInvitation(id),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.invitations }),
  });
}

export function useDevices() {
  return useQuery({ queryKey: qk.devices, queryFn: async () => (await devicesApi.list()).data.items });
}

export function useRevokeDevice() {
  const client = useQueryClient();
  return useMutation({ mutationFn: (id: string) => devicesApi.revoke(id), onSuccess: () => client.invalidateQueries({ queryKey: qk.devices }) });
}

export function useAuditLogs(f: { entityType?: string; actorEmployeeId?: string; from?: string; to?: string }) {
  return useQuery({ queryKey: qk.audit(f), queryFn: async () => (await auditApi.list({ ...f, limit: 50 })).data });
}
