# AlmoSeis — Contexto do Projeto

SaaS B2B2E mobile para gestão de pequenos restaurantes e marmitarias. Cobre pedidos de **marmita**, **PF** e **quilo**, cardápios diários e semanais, painel de produção da cozinha, pagamentos manuais (dinheiro/Pix/cartão), fechamento de caixa com conciliação e fiado. Assinatura mensal por estabelecimento com 7 dias grátis. Meta do MVP: piloto com 10 estabelecimentos reais. Épico completo em `docs/01-epico-ep001.md`.

## Antes de gerar qualquer código

Toda decisão de arquitetura, domínio e negócio está em `docs/adr/` (índice em `docs/adr/README.md`). **Os ADRs prevalecem sobre o texto do épico.** Nenhuma implementação pode divergir de um ADR sem que a divergência seja discutida e o ADR seja atualizado ou substituído antes. Se um documento estiver ambíguo ou incompleto para a tarefa, **pare e pergunte**. Não invente uma convenção silenciosamente.

ADRs com status `Proposto` já orientam a implementação, mas precisam ser revisados pelo usuário antes de o código que depende deles ir para produção. Ao tocar num deles, lembre o usuário.

## Mapa da documentação

| Arquivo | Conteúdo |
|---|---|
| `docs/01-epico-ep001.md` | Épico EP-001: requisitos (RF/RN/RNF), MVP, backlog, histórias |
| `docs/02-padroes.md` | Regras sempre/nunca por domínio e critérios de pronto |
| `docs/03-claude-code-agents.md` | Agentes, fluxo de desenvolvimento e próximas fases da documentação |
| `docs/04-modelo-dados.md` | Modelo de dados conceitual: entidades, campos, invariantes, cálculos derivados |
| `docs/05-ddl.md` | **DDL executável** PostgreSQL 16: tabelas, RLS, grants. Fonte de verdade do schema físico (+ `05-ddl-smoke-test.sql`) |
| `docs/adr/` | Decisões ADR-0001 a ADR-0039 |

## Decisões que mais pegam de surpresa

- O tenant vem **só** do JWT. Duas barreiras: query filter do EF + RLS do PostgreSQL (ADR-0026).
- Pedido e pagamento só existem com um **expediente (`BusinessDay`) aberto**. Pode haver vários expedientes na mesma data, um aberto por vez. O número do pedido reinicia por **data operacional** (ADR-0019, ADR-0023, ADR-0039). O cardápio semanal vira cardápio do dia na abertura do expediente (ADR-0038).
- Marmita: **preço por tamanho** + adicionais. PF: preço por prato. Quilo: gramas × preço/kg, arredondado por pesagem (ADR-0020, ADR-0030).
- Pagamento parcial, troco, divisão de conta e fiado via `Customer`. **Não existe desconto** no MVP (ADR-0021).
- Para cancelar um pedido, é preciso estornar os pagamentos antes. O estorno pode destravar o pedido (ADR-0022).
- Dinheiro trafega como **string** `"35.50"` na API. Peso como inteiro em gramas (ADR-0030).
- Escritas críticas exigem `Idempotency-Key`. Escritas em recurso existente exigem `If-Match` (ADR-0033, ADR-0034).
- Auditoria e outbox na mesma transação. SignalR é só aviso, e a recuperação de eventos perdidos é pelo `/sync/changes` (ADR-0031, ADR-0032).
- O app **nunca** mostra preço ou link de compra da assinatura (ADR-0017).

## Agentes especializados

Definidos em `.claude/agents/`:

- `architecture-agent`: revisão arquitetural e de aderência aos ADRs. Não gera código.
- `backend-agent`: C# / ASP.NET Core / EF Core / SignalR.
- `mobile-agent`: React Native / Expo / TypeScript.
- `database-agent`: modelagem física, migrations, RLS, índices, consultas de fechamento.
- `devops-agent`: Docker, GitHub Actions, Azure, EAS.
- `domain-agent`: regras de negócio de restaurante e financeiras; transforma lacunas em perguntas e ADRs. Não gera código.

Prefira acionar o agente do domínio em vez de gerar código de vários domínios na sessão principal.

## Fluxo de desenvolvimento

Toda funcionalidade segue esta ordem:

1. Confirmar regras e ADRs (`domain-agent` se houver lacuna);
2. Modelar entidades e migration (`database-agent`);
3. Definir o contrato da API (exemplos JSON no documento de contratos, quando existir);
4. Implementar o backend (`backend-agent`);
5. Implementar o mobile (`mobile-agent`);
6. Escrever os testes;
7. Revisar a arquitetura (`architecture-agent`).

## Estrutura do repositório (alvo)

```
AlmoSeis/
  docs/                 # épico, padrões, agentes, ADRs
  backend/              # solution .NET 10 (ADR-0029)
  mobile/               # app Expo
  infra/                # IaC Azure
  docker-compose.yml    # ambiente local
  .github/workflows/    # CI/CD
  .claude/agents/       # subagentes
```

## Convenções gerais

- Código em inglês. Interface em pt-BR. Banco em `snake_case`. JSON em `camelCase`, enums `UPPER_SNAKE`.
- Namespace raiz `AlmoSeis.*` (o épico usava `Restaurant.*`).
- Toda decisão nova relevante vira ADR (`docs/02-padroes.md` §2.7).
