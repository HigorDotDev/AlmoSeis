# ADR-0004 — Autenticação e autorização B2B2E com isolamento por tenant

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 RF-001, §16
- **Refinado por:** ADR-0026 (isolamento), ADR-0027 (identidade), ADR-0028 (permissões)

## Contexto

A plataforma atende vários estabelecimentos (tenants). Cada um tem funcionários com permissões próprias, e um mesmo usuário pode trabalhar em mais de um estabelecimento (RN-001.8).

## Decisão

- Cada estabelecimento é um `Tenant` com `TenantId`.
- `User` (identidade global) e `TenantEmployee` (vínculo usuário ↔ estabelecimento, com perfis e situação ativo/inativo) são entidades separadas.
- Toda requisição autenticada roda no contexto de **um único tenant ativo**, e toda ação registra usuário e tenant.
- A autorização é sempre validada no backend, nunca só pela visibilidade de botões.

## Alternativas consideradas

- **Usuário pertencente a um único tenant:** mais simples, mas contraria a RN-001.8 (freelancers que trabalham em mais de um restaurante).

## Consequências

- O detalhamento técnico está nos ADRs 0026, 0027 e 0028.
- Desativar um funcionário encerra o vínculo `TenantEmployee` daquele tenant, preserva o histórico (RN-001.7) e não afeta o usuário em outros tenants.
