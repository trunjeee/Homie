"""
Навык Алисы «Хоуми»: передаёт фразу на компьютер с Homie.

«Алиса, попроси Хоуми передать на балкон: ужин готов» → Homie на ПК говорит «Тебе передали: ужин готов».

Функция для Yandex Cloud Functions (Python 3.12, без зависимостей). Переменная окружения:
    HOMIE_KEY — «Ключ связи» из настроек Homie (раздел «Сообщения от Алисы»).
Необязательно:
    SKILL_ID  — ID навыка; если задан, запросы от других навыков отклоняются.

Сообщение подписывается ключом (HMAC-SHA256) и отправляется в канал ntfy.sh, имя которого
тоже выводится из ключа. Homie принимает только сообщения с верной подписью.
"""

import hashlib
import hmac
import json
import os
import re
import time
import urllib.request

# Слова-команды в начале фразы и «куда»: их не передаём, передаём только само сообщение.
LEAD = re.compile(
    r"^(?:пожалуйста\s+)?(?:передай(?:те)?|передать|скажи(?:те)?|сказать|сообщи(?:те)?|сообщить|напиши|написать)\s+",
    re.IGNORECASE,
)
WHERE = re.compile(
    r"^(?:(?:на|в|во)\s+(?:балкон|компьютер|комп|пк|ноутбук)\w*|компьютеру|на\s+пк)\s*[:,\-—]?\s*",
    re.IGNORECASE,
)
QUOTES = "«»\"'“”"


def topic_for(key: str) -> str:
    """Имя канала ntfy из ключа (так же считает Homie)."""
    return "homie-" + hashlib.sha256(("topic:" + key).encode()).hexdigest()[:32]


def send_to_homie(key: str, text: str) -> None:
    ts = int(time.time())
    sig = hmac.new(key.encode(), f"{ts}\n{text}".encode(), hashlib.sha256).hexdigest()
    # Только ASCII (кириллица как \uXXXX): ntfy не спутает сообщение с файлом ни при какой кодировке.
    body = json.dumps({"text": text, "ts": ts, "sig": sig}, ensure_ascii=True).encode("ascii")
    req = urllib.request.Request(f"https://ntfy.sh/{topic_for(key)}", data=body, method="POST")
    urllib.request.urlopen(req, timeout=2.5).read()


def extract_message(utterance: str) -> str:
    text = utterance.strip()
    text = LEAD.sub("", text)
    text = WHERE.sub("", text)
    return text.strip().strip(QUOTES).strip()


def reply(text: str, end: bool = True, state: dict | None = None) -> dict:
    resp = {"version": "1.0", "response": {"text": text, "end_session": end}}
    if state is not None:
        resp["session_state"] = state
    return resp


def handler(event, context):
    key = os.environ.get("HOMIE_KEY", "")
    if not key:
        return reply("Навык не настроен: добавь переменную HOMIE_KEY.")

    skill_id = os.environ.get("SKILL_ID")
    session = event.get("session") or {}
    if skill_id and session.get("skill_id") != skill_id:
        return reply("Неизвестный навык.")

    request = event.get("request") or {}
    utterance = (request.get("original_utterance") or request.get("command") or "").strip()
    state = (event.get("state") or {}).get("session") or {}

    # «Алиса, запусти Хоуми» без текста — спрашиваем, что передать.
    if session.get("new") and not utterance:
        return reply("Что передать на компьютер?", end=False, state={"waiting": True})

    message = utterance if state.get("waiting") else extract_message(utterance)
    if not message:
        return reply("Что передать на компьютер?", end=False, state={"waiting": True})
    if message.lower() in ("отмена", "стоп", "ничего", "не надо", "выход"):
        return reply("Хорошо, ничего не передаю.")

    try:
        send_to_homie(key, message)
    except Exception:  # noqa: BLE001 — Алисе нужен ответ в любом случае
        return reply("Не получилось достучаться до компьютера, попробуй ещё раз.")
    return reply("Передала.")
