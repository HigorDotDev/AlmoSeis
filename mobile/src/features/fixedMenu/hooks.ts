/**
 * Cardápio fixo (06 §6.5, EP-003 fatia 2). Escritas de gestão (08 §8.14): mutação simples, sempre com `If-Match`,
 * sem `Idempotency-Key` (fora do ADR-0033). Toda escrita recarrega o `GET /fixed-menu` **inteiro**: pausar ou
 * inativar uma raiz muda a disponibilidade derivada de outras (combos).
 */
import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';

import { fixedMenuApi } from '@/api/endpoints/fixedMenu';
import { isApiError } from '@/api/errors';
import { qk } from '@/api/queryKeys';
import type {
  AddonAvailabilityBody,
  AddonGroup,
  BeverageAvailabilityBody,
  BeverageType,
  Combo,
  ComboAvailabilityBody,
  CreateAddonGroupBody,
  CreateBeverageTypeBody,
  CreateComboBody,
  CreateDishBody,
  Dish,
  DishAvailabilityBody,
  DishCategory,
  FixedMenu,
  UpdateAddonGroupBody,
  UpdateBeverageTypeBody,
  UpdateComboBody,
  UpdateDishBody,
  Uuid,
} from '@/api/types';
import { uploadJpeg } from '@/features/catalog/imageUpload';

const STALE = 2 * 60_000;

export function useFixedMenu(includeInactive = true) {
  return useQuery({
    queryKey: qk.fixedMenu(includeInactive),
    queryFn: async () => (await fixedMenuApi.get(includeInactive)).data,
    staleTime: STALE,
  });
}

type RootList = 'categories' | 'dishes' | 'beverageTypes' | 'combos' | 'addonGroups';
type Root = DishCategory | Dish | BeverageType | Combo | AddonGroup;

/** Troca a raiz devolvida pela API nas listas em cache (a tela não pisca) e recarrega o cardápio inteiro. */
function putRoot(client: QueryClient, list: RootList, root: Root) {
  client.setQueriesData<FixedMenu>({ queryKey: ['fixedMenu'] }, (menu) => {
    if (!menu) return menu;
    const items = menu[list] as Root[];
    const exists = items.some((i) => i.id === root.id);
    return { ...menu, [list]: exists ? items.map((i) => (i.id === root.id ? root : i)) : [...items, root] };
  });
}

async function refresh(client: QueryClient) {
  await client.invalidateQueries({ queryKey: ['fixedMenu'] });
}

/**
 * `409 VersionConflict`: o `current` entra no cache e o cardápio é recarregado **antes** do `onError` da tela,
 * que então remonta o formulário com a versão atual. Nada é reenviado.
 */
async function onWriteError(client: QueryClient, list: RootList, error: unknown) {
  if (isApiError(error, 'VersionConflict')) {
    const current = error.extensions.current as Root | undefined;
    if (current && typeof current === 'object' && 'id' in current) putRoot(client, list, current);
    await client.refetchQueries({ queryKey: ['fixedMenu'], type: 'active' });
  }
}

/** Criar (`id` ausente) ou alterar (`id` + `version` → `If-Match`). */
export type SaveVars<TCreate, TUpdate> = { id?: undefined; version?: undefined; body: TCreate } | { id: Uuid; version: number; body: TUpdate };

function useRootMutation<TCreate, TUpdate, TRoot extends Root>(
  list: RootList,
  create: (body: TCreate) => Promise<{ data: TRoot }>,
  update: (id: Uuid, body: TUpdate, version: number) => Promise<{ data: TRoot }>,
) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (v: SaveVars<TCreate, TUpdate>) => (v.id !== undefined ? (await update(v.id, v.body, v.version)).data : (await create(v.body)).data),
    onSuccess: async (root) => {
      putRoot(client, list, root);
      await refresh(client);
    },
    onError: (error) => onWriteError(client, list, error),
  });
}

export function useSaveCategory() {
  return useRootMutation<{ name: string; sortOrder: number }, Partial<{ name: string; sortOrder: number; isActive: boolean }>, DishCategory>(
    'categories',
    (body) => fixedMenuApi.createCategory(body),
    (id, body, version) => fixedMenuApi.updateCategory(id, body, version),
  );
}

/** Reordenar: um `PATCH` por categoria que mudou de `sortOrder`, em série (cada uma com a sua versão). */
export function useReorderCategories() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (patches: { id: Uuid; version: number; sortOrder: number }[]) => {
      for (const p of patches) {
        putRoot(client, 'categories', (await fixedMenuApi.updateCategory(p.id, { sortOrder: p.sortOrder }, p.version)).data);
      }
    },
    onError: (error) => onWriteError(client, 'categories', error),
    onSettled: () => refresh(client),
  });
}

export function useSaveDish() {
  return useRootMutation<CreateDishBody, UpdateDishBody, Dish>('dishes', fixedMenuApi.createDish, fixedMenuApi.updateDish);
}

export function useSaveBeverageType() {
  return useRootMutation<CreateBeverageTypeBody, UpdateBeverageTypeBody, BeverageType>('beverageTypes', fixedMenuApi.createBeverageType, fixedMenuApi.updateBeverageType);
}

export function useSaveCombo() {
  return useRootMutation<CreateComboBody, UpdateComboBody, Combo>('combos', fixedMenuApi.createCombo, fixedMenuApi.updateCombo);
}

export function useSaveAddonGroup() {
  return useRootMutation<CreateAddonGroupBody, UpdateAddonGroupBody, AddonGroup>('addonGroups', fixedMenuApi.createAddonGroup, fixedMenuApi.updateAddonGroup);
}

/** Pausar e reativar (`catalog.availability`, Q-306): uma rota por raiz, com `If-Match` da raiz. */
export type PauseRequest =
  | { kind: 'dish'; id: Uuid; version: number; body: DishAvailabilityBody; label: string; paused: boolean }
  | { kind: 'beverage'; id: Uuid; version: number; body: BeverageAvailabilityBody; label: string; paused: boolean }
  | { kind: 'combo'; id: Uuid; version: number; body: ComboAvailabilityBody; label: string; paused: boolean }
  | { kind: 'addon'; id: Uuid; version: number; body: AddonAvailabilityBody; label: string; paused: boolean };

const PAUSE_LIST: Record<PauseRequest['kind'], RootList> = { dish: 'dishes', beverage: 'beverageTypes', combo: 'combos', addon: 'addonGroups' };

export function usePauseFixedMenuItem() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (r: PauseRequest): Promise<Root> => {
      switch (r.kind) {
        case 'dish':
          return (await fixedMenuApi.dishAvailability(r.id, r.body, r.version)).data;
        case 'beverage':
          return (await fixedMenuApi.beverageAvailability(r.id, r.body, r.version)).data;
        case 'combo':
          return (await fixedMenuApi.comboAvailability(r.id, r.body, r.version)).data;
        case 'addon':
          return (await fixedMenuApi.addonAvailability(r.id, r.body, r.version)).data;
      }
    },
    onSuccess: async (root, r) => {
      putRoot(client, PAUSE_LIST[r.kind], root);
      // Os combos que usam o item mudam de `isSellable` sem mudar de versão (06 §6.5).
      await refresh(client);
    },
    onError: (error, r) => onWriteError(client, PAUSE_LIST[r.kind], error),
  });
}

/** Foto de prato ou combo: `…/image-upload` → `PUT` no R2 → `PATCH imageStorageKey` (chaves `d/` e `c/`, 06 §6.5). */
export function useUploadFixedMenuImage() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (v: { kind: 'dish' | 'combo'; id: Uuid; version: number; uri: string }): Promise<Root> => {
      if (v.kind === 'dish') {
        const key = await uploadJpeg(v.uri, (type, size) => fixedMenuApi.dishImageUpload(v.id, type, size));
        return (await fixedMenuApi.updateDish(v.id, { imageStorageKey: key }, v.version)).data;
      }
      const key = await uploadJpeg(v.uri, (type, size) => fixedMenuApi.comboImageUpload(v.id, type, size));
      return (await fixedMenuApi.updateCombo(v.id, { imageStorageKey: key }, v.version)).data;
    },
    onSuccess: async (root, v) => {
      putRoot(client, v.kind === 'dish' ? 'dishes' : 'combos', root);
      await refresh(client);
    },
    onError: (error, v) => onWriteError(client, v.kind === 'dish' ? 'dishes' : 'combos', error),
  });
}
