/** Validação dos formulários de pedido antes da confirmação (docs/02-padroes.md §2.3: RHF + Zod). */
import { z } from 'zod';

import { toE164 } from '@/lib/phone';

const address = z.object({
  street: z.string(),
  number: z.string().nullable(),
  complement: z.string().nullable(),
  neighborhood: z.string().nullable(),
  reference: z.string().nullable(),
});

const addon = z.object({ productId: z.string(), quantity: z.number().int().min(0).max(9) });

export const marmitaFormSchema = z
  .object({
    customer: z.object({ id: z.string().nullable(), name: z.string(), phone: z.string(), saveCustomer: z.boolean() }),
    fulfillmentType: z.enum(['DELIVERY', 'PICKUP']),
    addressId: z.string().nullable(),
    address: address.nullable(),
    deliveryFee: z.string().nullable(),
    lines: z.array(
      z.object({
        key: z.string(),
        sizeId: z.string(),
        quantity: z.number().int().min(1).max(99),
        mainDishIds: z.array(z.string()),
        sideDishIds: z.array(z.string()),
        notes: z.string(),
        addons: z.array(addon),
      }),
    ),
    beverages: z.record(z.string(), z.number().int().min(0).max(99)),
    notes: z.string(),
  })
  .superRefine((f, ctx) => {
    const beverageCount = Object.values(f.beverages).reduce((a, b) => a + b, 0);
    if (f.lines.length === 0 && beverageCount === 0) {
      ctx.addIssue({ code: 'custom', path: ['lines'], message: 'Inclua pelo menos uma marmita ou bebida.' });
    }
    f.lines.forEach((l, i) => {
      if (l.mainDishIds.length === 0) ctx.addIssue({ code: 'custom', path: ['lines', i], message: 'Escolha o prato principal.' });
    });
    if (f.customer.phone.trim() !== '' && toE164(f.customer.phone) === null) {
      ctx.addIssue({ code: 'custom', path: ['customer', 'phone'], message: 'Telefone com DDD.' });
    }
    if (f.customer.saveCustomer && f.customer.name.trim() === '') {
      ctx.addIssue({ code: 'custom', path: ['customer', 'name'], message: 'Informe o nome para salvar o cliente.' });
    }
    if (f.fulfillmentType === 'DELIVERY') {
      if (f.customer.name.trim() === '') ctx.addIssue({ code: 'custom', path: ['customer', 'name'], message: 'Informe o cliente da entrega.' });
      if (!f.addressId && (!f.address || f.address.street.trim() === '')) {
        ctx.addIssue({ code: 'custom', path: ['address'], message: 'Informe o endereço de entrega.' });
      }
    }
  });

export const pfFormSchema = z
  .object({
    tableLabel: z.string(),
    tabNumber: z.string(),
    lines: z.array(z.object({ key: z.string(), productId: z.string(), quantity: z.number().int().min(1).max(99), notes: z.string(), addons: z.array(addon) })),
    beverages: z.record(z.string(), z.number().int().min(0).max(99)),
    notes: z.string(),
  })
  .superRefine((f, ctx) => {
    if (f.tableLabel.trim() === '' && f.tabNumber.trim() === '') ctx.addIssue({ code: 'custom', path: ['tableLabel'], message: 'Informe a mesa ou a comanda.' });
    if (f.lines.length === 0 && Object.values(f.beverages).every((q) => q === 0)) {
      ctx.addIssue({ code: 'custom', path: ['lines'], message: 'Inclua pelo menos um prato ou bebida.' });
    }
  });

export const kiloFormSchema = z
  .object({
    tableLabel: z.string(),
    tabNumber: z.string(),
    weighings: z.array(z.number().int().min(1).max(50_000)),
    beverages: z.record(z.string(), z.number().int().min(0).max(99)),
    notes: z.string(),
  })
  .superRefine((f, ctx) => {
    if (f.tableLabel.trim() === '' && f.tabNumber.trim() === '') ctx.addIssue({ code: 'custom', path: ['tableLabel'], message: 'Informe a mesa ou a comanda.' });
    if (f.weighings.length === 0 && Object.values(f.beverages).every((q) => q === 0)) {
      ctx.addIssue({ code: 'custom', path: ['weighings'], message: 'Inclua pelo menos uma pesagem ou bebida.' });
    }
  });

/** Primeira mensagem de erro, para mostrar no rodapé. */
export function firstIssue(result: { success: boolean; error?: z.ZodError }): string | null {
  return result.success ? null : (result.error?.issues[0]?.message ?? 'Confira o pedido.');
}
