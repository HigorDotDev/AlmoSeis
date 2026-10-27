# ADR-0003 — PostgreSQL como banco de dados central

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** EP-001 §5.1, §16
- **Relacionados:** ADR-0026 (RLS), ADR-0030 (tipos numéricos), ADR-0037 (hospedagem)

## Contexto

Os dados são fortemente relacionais e financeiros: pedidos, itens, pagamentos e fechamentos exigem transações ACID, restrições de integridade e controle de concorrência.

## Decisão

Uma única instância de **PostgreSQL**, acessada via EF Core (Npgsql), com nomes de tabela e coluna em `snake_case`. O banco é a **fonte oficial** de todo estado (EP-001 §8).

## Alternativas consideradas

- **SQL Server / Azure SQL:** licença e custo maiores, sem ganho funcional para este domínio.
- **NoSQL (documentos):** as transações entre pedido, pagamento e caixa e os relatórios agregados ficariam mais difíceis.

## Consequências

- Recursos do PostgreSQL usados de forma deliberada: `numeric`, `timestamptz`, Row-Level Security (ADR-0026), `UPDATE ... RETURNING` para sequências por tenant (ADR-0023, ADR-0032).
- Backups e restauração ponto-a-ponto vêm do serviço gerenciado (ADR-0037).
