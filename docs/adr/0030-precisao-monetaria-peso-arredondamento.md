# ADR-0030 — Precisão monetária, peso e regra de arredondamento

- **Status:** Aceito (recomendação aceita pelo usuário em 2026-10-27)
- **Data:** 2026-10-27
- **Origem:** Lacuna em RN-006.8, RNF-004 ("precisão decimal definida")
- **Relacionados:** ADR-0020, ADR-0021

## Contexto

O épico exige uma precisão definida, mas não a define. O app em JavaScript tem `number` em ponto flutuante, e a multiplicação peso × preço/kg precisa de uma regra de arredondamento única entre cliente e servidor.

## Decisão

1. **Moeda:** só BRL. Banco `numeric(12,2)`, C# `decimal` encapsulado no value object `Money`.
2. **Peso:** **inteiro em gramas** (`weight_grams integer`, de 1 a 50.000). Balanças comerciais mostram 3 casas em kg, então gramas representam o valor exatamente.
3. **Preço por kg:** `numeric(10,2)`.
4. **Valor de pesagem:** `round(weight_grams × price_per_kg / 1000, 2, MidpointRounding.AwayFromZero)`. Cada pesagem é arredondada individualmente, e o total da comanda é a soma dos valores já arredondados.
5. **Na API:** valores monetários trafegam como **string decimal com 2 casas** (`"35.50"`). Peso trafega como inteiro (`"weightGrams": 452`).
6. **No app:** o app converte para **centavos inteiros** nos cálculos de exibição (`35.50` → `3550`) e **nunca usa ponto flutuante** em valores. O valor oficial é sempre o calculado pelo servidor.

## Alternativas consideradas

- **Valores como número JSON:** risco de `0.1 + 0.2` no cliente.
- **Centavos inteiros na API:** seguro, mas menos legível e fácil de confundir em integrações futuras.

## Consequências

- Os validadores rejeitam mais de 2 casas decimais em valores (`422`).
- Testes de propriedade cobrem a regra de arredondamento.
