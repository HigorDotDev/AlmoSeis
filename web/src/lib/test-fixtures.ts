import type { PublicFixedMenu } from './api/types';

/** Fixtures dos testes (não entram no bundle: só os testes importam). */

// Cardápio fixo do exemplo do contrato (06 §6.19, "Lanchonete do Zé"), com ids curtos.
export const fixedMenu: PublicFixedMenu = {
  categories: [
    {
      id: 'cat-lanches',
      name: 'Lanches',
      dishes: [
        {
          id: 'xburguer',
          name: 'X-Burguer',
          description: 'Pão, hambúrguer 150 g, queijo',
          imageUrl: null,
          hasSizes: false,
          price: '23.90',
          sizes: [],
          addonGroupIds: ['grp-lanche'],
        },
      ],
    },
    {
      id: 'cat-porcoes',
      name: 'Porções',
      dishes: [
        {
          id: 'batata',
          name: 'Batata frita',
          description: null,
          imageUrl: null,
          hasSizes: true,
          price: null,
          sizes: [
            { id: 'batata-p', name: 'P', price: '13.00' },
            { id: 'batata-g', name: 'G', price: '19.00' },
          ],
          addonGroupIds: [],
        },
      ],
    },
  ],
  beverages: [
    {
      id: 'refri',
      name: 'Refrigerante',
      sizes: [
        {
          id: 'lata',
          name: 'Lata 350 ml',
          price: '6.50',
          flavors: [
            { id: 'coca', name: 'Coca-Cola', extraPrice: '0.00' },
            { id: 'zero', name: 'Coca-Cola Zero', extraPrice: '0.50' },
          ],
        },
      ],
    },
    { id: 'agua', name: 'Água', sizes: [{ id: 'agua-500', name: '500 ml', price: '4.00', flavors: [] }] },
  ],
  combos: [
    {
      id: 'combo',
      name: 'X-Burguer + batata + refri',
      description: null,
      imageUrl: null,
      price: '34.90',
      components: [
        { id: 'c-x', kind: 'DISH', name: 'X-Burguer', quantity: 1, addonGroupIds: ['grp-lanche'], flavors: [] },
        { id: 'c-batata', kind: 'DISH', name: 'Batata frita P', quantity: 1, addonGroupIds: [], flavors: [] },
        {
          id: 'c-refri',
          kind: 'BEVERAGE',
          name: 'Refrigerante Lata 350 ml',
          quantity: 1,
          addonGroupIds: [],
          flavors: [
            { id: 'coca', name: 'Coca-Cola', extraPrice: '0.00' },
            { id: 'zero', name: 'Coca-Cola Zero', extraPrice: '0.50' },
          ],
        },
      ],
    },
  ],
  addonGroups: [
    {
      id: 'grp-lanche',
      name: 'Adicionais de lanche',
      options: [
        { id: 'bacon', name: 'Bacon', price: '4.00' },
        { id: 'ovo', name: 'Ovo', price: '2.50' },
      ],
    },
  ],
};
