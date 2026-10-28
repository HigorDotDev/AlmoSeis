/** Clientes e fiado (06 §6.8). */
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { customersApi } from '@/api/endpoints/customers';
import { qk } from '@/api/queryKeys';
import type { CreateCustomerBody, Customer, CustomerAddressInput } from '@/api/types';

const STALE = 60_000;

export function useCustomerSearch(term: string, enabled = true) {
  return useInfiniteQuery({
    queryKey: qk.customerSearch(term),
    queryFn: async ({ pageParam }) => (await customersApi.search({ search: term || undefined, limit: 20, cursor: pageParam })).data,
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    staleTime: STALE,
    enabled,
  });
}

export function useCustomer(id: string | undefined) {
  return useQuery({ queryKey: qk.customer(id ?? ''), queryFn: async () => (await customersApi.get(id!)).data, enabled: !!id, staleTime: STALE });
}

export function useCustomerPending(id: string | undefined) {
  return useQuery({
    queryKey: qk.customerPending(id ?? ''),
    queryFn: async () => (await customersApi.pendingOrders(id!)).data,
    enabled: !!id,
    staleTime: STALE,
  });
}

function setCustomer(client: ReturnType<typeof useQueryClient>, c: Customer) {
  client.setQueryData(qk.customer(c.id), c);
  void client.invalidateQueries({ queryKey: ['customers', 'search'] });
}

export function useCreateCustomer() {
  const client = useQueryClient();
  return useMutation({ mutationFn: async (body: CreateCustomerBody) => (await customersApi.create(body)).data, onSuccess: (c) => setCustomer(client, c) });
}

export function useUpdateCustomer() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, body, version }: { id: string; body: { name?: string; phoneNumber?: string | null; notes?: string | null }; version: number }) =>
      (await customersApi.update(id, body, version)).data,
    onSuccess: (c) => setCustomer(client, c),
  });
}

export function useAddressMutations() {
  const client = useQueryClient();
  const onSuccess = (c: Customer) => setCustomer(client, c);
  return {
    add: useMutation({
      mutationFn: async ({ customer, body }: { customer: Customer; body: CustomerAddressInput }) =>
        (await customersApi.addAddress(customer.id, body, customer.version)).data,
      onSuccess,
    }),
    update: useMutation({
      mutationFn: async ({ customer, addressId, body }: { customer: Customer; addressId: string; body: Partial<CustomerAddressInput> }) =>
        (await customersApi.updateAddress(customer.id, addressId, body, customer.version)).data,
      onSuccess,
    }),
    remove: useMutation({
      mutationFn: async ({ customer, addressId }: { customer: Customer; addressId: string }) =>
        (await customersApi.deleteAddress(customer.id, addressId, customer.version)).data,
      onSuccess,
    }),
  };
}

export function useAnonymizeCustomer() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ customer, reason }: { customer: Customer; reason: string }) => (await customersApi.anonymize(customer.id, reason, customer.version)).data,
    onSuccess: (c) => {
      setCustomer(client, c);
      void client.invalidateQueries({ queryKey: ['orders'] });
    },
  });
}
