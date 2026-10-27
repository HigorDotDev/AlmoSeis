# ADR-0024 — LGPD: papéis de tratamento, minimização e retenção

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em RNF-003 ("práticas compatíveis com a LGPD"), RN-013.6
- **Relacionados:** ADR-0017, ADR-0021, ADR-0031

## Contexto

A plataforma trata dados de três grupos: proprietários e funcionários (usuários), consumidores finais (nome, telefone e endereço nos pedidos) e dados financeiros do estabelecimento. O épico só diz "compatível com LGPD".

## Decisão

1. **Papéis:**
   - Para os dados de **consumidores finais**, o estabelecimento é **controlador** e a AlmoSeis é **operadora**. Isso fica formalizado num contrato de tratamento de dados aceito no cadastro.
   - Para os dados de **usuários** (login, perfis, auditoria de ações), a AlmoSeis é controladora.
2. **Minimização:**
   - `Customer` guarda só nome, telefone, endereço e observação.
   - **Auditoria e logs não gravam telefone nem endereço.** Referenciam `CustomerId`.
   - Logs operacionais não gravam payloads de pedido nem tokens.
3. **Anonimização sob demanda:** o estabelecimento pode anonimizar um cliente (nome → "Cliente anonimizado", telefone e endereço apagados). Snapshots nos pedidos são anonimizados juntos. Valores financeiros são preservados.
4. **Retenção pós-cancelamento:**
   - Tenant `CANCELED` mantém os dados por **90 dias** (com exportação em CSV disponível).
   - Depois disso, os dados pessoais de consumidores e funcionários são anonimizados.
   - Registros financeiros agregados e a auditoria anonimizada são mantidos pelo prazo legal aplicável.
5. Os dados ficam no Brasil (ADR-0037).

## Consequências

- O texto dos Termos e do contrato de tratamento depende de revisão jurídica antes do piloto.
- É preciso um job agendado de retenção e um endpoint de exportação do tenant.
