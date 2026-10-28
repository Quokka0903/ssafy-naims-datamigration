# -*- coding: utf-8 -*-
"""
build_mapping.py 의 매핑 규칙을 단일 JSON(SSOT)으로 추출한다.
이 JSON 을 Python(문서 생성)과 C#(실행기) 이 함께 읽는다.

출력 위치: RawCollectMigrator/Features/DbToDb/mapping_rules.json  (C# 프로젝트에 동봉)
"""
import io, os, json, collections
import build_mapping as B

HERE = os.path.dirname(os.path.abspath(__file__))
# docs/schema → 프로젝트 루트 → RawCollectMigrator/Features/DbToDb
OUT = os.path.abspath(os.path.join(
    HERE, "..", "..", "RawCollectMigrator", "Features", "DbToDb", "mapping_rules.json"))


def main():
    data = collections.OrderedDict()
    data["_comment"] = ("NAIMS→NAIMS2 DB→DB 매핑 규칙 단일 원본(SSOT). "
                        "docs/schema/build_mapping.py 와 C# MigrationRules.cs 가 함께 읽는다. "
                        "생성: docs/schema/dump_rules_json.py")
    data["version"] = "2026-07-23"

    # 이관 계획 (순서/대상/PK/단계/분류)
    data["plan"] = [
        collections.OrderedDict([
            ("source", s), ("target", t), ("pk", pk), ("level", lvl), ("category", cat)])
        for s, t, pk, lvl, cat in B.PLAN
    ]

    data["excluded"] = collections.OrderedDict(sorted(B.EXCLUDED.items()))
    data["referenceOnly"] = collections.OrderedDict(sorted(B.REFERENCE_ONLY.items()))
    data["dropped"] = collections.OrderedDict(B.DROPPED)
    data["aliases"] = collections.OrderedDict(B.ALIASES)
    data["targetOnlyNotes"] = collections.OrderedDict(B.TARGET_ONLY)

    # 수동 컬럼 대응: (src,col) → [target, rule, note]  (None → null)
    data["manual"] = [
        collections.OrderedDict([
            ("source", s), ("column", c),
            ("target", man[0]), ("rule", man[1]), ("note", man[2])])
        for (s, c), man in B.MANUAL.items()
    ]

    # 타겟 컬럼별 규칙: (tgt,tcol) → {rule,verdict,note}
    data["columnRules"] = [
        collections.OrderedDict([
            ("target", t), ("column", c),
            ("rule", d["rule"]), ("verdict", d["verdict"]), ("note", d["note"])])
        for (t, c), d in B.COLUMN_RULES.items()
    ]

    # 고정 기본값: (tgt,tcol) → value
    data["targetFixedDefault"] = [
        collections.OrderedDict([("target", t), ("column", c), ("value", v)])
        for (t, c), v in B.TARGET_FIXED_DEFAULT.items()
    ]

    data["classesFillDefault"] = collections.OrderedDict([
        ("value", "-1"),
        ("except", sorted(B.CLASSES_FILL_EXCEPT)),
        ("note", "STD_CLASSES 소스 대응 없는 신규 컬럼 임시 기본값"),
    ])

    # 암호화 컬럼
    data["encrypted"] = collections.OrderedDict([
        ("mssql", sorted(B.ENC_MSSQL)),
        ("oracle", sorted(B.ENC_ORACLE)),
    ])

    # 구조화 코드 매핑
    data["gramTypeMap"] = collections.OrderedDict(B.GRAM_TYPE_MAP)
    data["refTargetTypeCodes"] = collections.OrderedDict(B.REF_TARGET_TYPE_CODES)

    with io.open(OUT, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print("saved: %s" % OUT)
    print("plan=%d excluded=%d manual=%d columnRules=%d" % (
        len(data["plan"]), len(data["excluded"]),
        len(data["manual"]), len(data["columnRules"])))


if __name__ == "__main__":
    main()
