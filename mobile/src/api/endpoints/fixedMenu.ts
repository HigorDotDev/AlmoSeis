import { apiRequest, qs } from '../client';
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
  ImageUpload,
  UpdateAddonGroupBody,
  UpdateBeverageTypeBody,
  UpdateComboBody,
  UpdateDishBody,
  Uuid,
} from '../types';

/**
 * 06 §6.5 "Cardápio fixo" (EP-003 fatia 2). Cinco raízes com versão própria (`If-Match`); nenhuma rota exige
 * `Idempotency-Key`. Pausa só pelas rotas `…/availability` (`catalog.availability`).
 */
export const fixedMenuApi = {
  get: (includeInactive = false) => apiRequest<FixedMenu>('GET', `/fixed-menu${qs({ includeInactive })}`),

  createCategory: (body: { name: string; sortOrder: number }) => apiRequest<DishCategory>('POST', '/fixed-menu/dish-categories', { body }),
  updateCategory: (id: Uuid, body: Partial<{ name: string; sortOrder: number; isActive: boolean }>, version: number) =>
    apiRequest<DishCategory>('PATCH', `/fixed-menu/dish-categories/${id}`, { body, ifMatch: version }),

  createDish: (body: CreateDishBody) => apiRequest<Dish>('POST', '/fixed-menu/dishes', { body }),
  updateDish: (id: Uuid, body: UpdateDishBody, version: number) => apiRequest<Dish>('PATCH', `/fixed-menu/dishes/${id}`, { body, ifMatch: version }),
  dishAvailability: (id: Uuid, body: DishAvailabilityBody, version: number) =>
    apiRequest<Dish>('PATCH', `/fixed-menu/dishes/${id}/availability`, { body, ifMatch: version }),
  dishImageUpload: (id: Uuid, contentType: string, sizeBytes: number) =>
    apiRequest<ImageUpload>('POST', `/fixed-menu/dishes/${id}/image-upload`, { body: { contentType, sizeBytes } }),

  createBeverageType: (body: CreateBeverageTypeBody) => apiRequest<BeverageType>('POST', '/fixed-menu/beverage-types', { body }),
  updateBeverageType: (id: Uuid, body: UpdateBeverageTypeBody, version: number) =>
    apiRequest<BeverageType>('PATCH', `/fixed-menu/beverage-types/${id}`, { body, ifMatch: version }),
  beverageAvailability: (id: Uuid, body: BeverageAvailabilityBody, version: number) =>
    apiRequest<BeverageType>('PATCH', `/fixed-menu/beverage-types/${id}/availability`, { body, ifMatch: version }),

  createCombo: (body: CreateComboBody) => apiRequest<Combo>('POST', '/fixed-menu/combos', { body }),
  updateCombo: (id: Uuid, body: UpdateComboBody, version: number) => apiRequest<Combo>('PATCH', `/fixed-menu/combos/${id}`, { body, ifMatch: version }),
  comboAvailability: (id: Uuid, body: ComboAvailabilityBody, version: number) =>
    apiRequest<Combo>('PATCH', `/fixed-menu/combos/${id}/availability`, { body, ifMatch: version }),
  comboImageUpload: (id: Uuid, contentType: string, sizeBytes: number) =>
    apiRequest<ImageUpload>('POST', `/fixed-menu/combos/${id}/image-upload`, { body: { contentType, sizeBytes } }),

  createAddonGroup: (body: CreateAddonGroupBody) => apiRequest<AddonGroup>('POST', '/fixed-menu/addon-groups', { body }),
  updateAddonGroup: (id: Uuid, body: UpdateAddonGroupBody, version: number) =>
    apiRequest<AddonGroup>('PATCH', `/fixed-menu/addon-groups/${id}`, { body, ifMatch: version }),
  addonAvailability: (id: Uuid, body: AddonAvailabilityBody, version: number) =>
    apiRequest<AddonGroup>('PATCH', `/fixed-menu/addon-groups/${id}/availability`, { body, ifMatch: version }),
};
