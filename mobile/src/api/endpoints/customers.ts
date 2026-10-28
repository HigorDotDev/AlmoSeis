import { apiRequest, qs } from '../client';
import type { CreateCustomerBody, Customer, CustomerAddressInput, Page, PendingOrders, Uuid } from '../types';

/** 06 §6.8. */
export const customersApi = {
  search: (f: { search?: string; limit?: number; cursor?: string }) => apiRequest<Page<Customer>>('GET', `/customers${qs(f)}`),
  get: (id: Uuid) => apiRequest<Customer>('GET', `/customers/${id}`),
  create: (body: CreateCustomerBody) => apiRequest<Customer>('POST', '/customers', { body }),
  update: (id: Uuid, body: { name?: string; phoneNumber?: string | null; notes?: string | null }, version: number) =>
    apiRequest<Customer>('PATCH', `/customers/${id}`, { body, ifMatch: version }),
  addAddress: (id: Uuid, body: CustomerAddressInput, version: number) =>
    apiRequest<Customer>('POST', `/customers/${id}/addresses`, { body, ifMatch: version }),
  updateAddress: (id: Uuid, addressId: Uuid, body: Partial<CustomerAddressInput>, version: number) =>
    apiRequest<Customer>('PATCH', `/customers/${id}/addresses/${addressId}`, { body, ifMatch: version }),
  deleteAddress: (id: Uuid, addressId: Uuid, version: number) =>
    apiRequest<Customer>('DELETE', `/customers/${id}/addresses/${addressId}`, { ifMatch: version }),
  anonymize: (id: Uuid, reason: string, version: number) =>
    apiRequest<Customer>('POST', `/customers/${id}/anonymize`, { body: { reason }, ifMatch: version }),
  pendingOrders: (id: Uuid) => apiRequest<PendingOrders>('GET', `/customers/${id}/pending-orders`),
};
