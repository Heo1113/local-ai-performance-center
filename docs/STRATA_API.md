# Strata 연동 근거

확인한 원본: https://github.com/Niko1221/Strata (MIT), 커밋 `c62d5e3` (2026-10-31), 문서상 앱 0.2.9 / 엔진 0.1.55.

## 사용하는 공개 엔드포인트 (모두 GET, 읽기 전용)

| 엔드포인트 | 근거 문서 | 쓰는 값 | 인증 |
|---|---|---|---|
| `GET /v1/telemetry` | `docs/TERMINAL_TELEMETRY_FIELDS.md` ("authoritative read-only response"), `docs/TERMINAL.md` | `schema_version`(1만 허용), `sampled_at`, `instance`, `model`, `work`(슬롯·대기·거부·시간 초과), `rates`(answer/prefill tok/s, 출력 카운터), `recent[]`(완료 요청 수치), `totals`, `activity.events[]` | API 키 또는 읽기 전용 텔레메트리 키 |
| `GET /metrics` + `GET /v1/status` | 같은 문서: v1이 **404일 때만** 대체 | 구버전 서버의 상태·속도·최근 요청 | API 키 |
| `GET /v1/status` (단독) | 같은 문서: `/metrics`도 404일 때 "status-only" | 모델·컨텍스트·엔진 버전 (실시간 값 없음 → "제한적 연결") | API 키 |
| `GET /health` | `docs/DETAILS.md` Quick start / Using it | 서비스 이름, 로드 상태, API 키 설정 여부 | 없음 |
| `GET /v1/status` | `serve/server.py` `v1_status()` | 엔진 버전, 가동 시간, 동시 처리 슬롯, 비전 | API 키 |
| `GET /props` | `docs/DETAILS.md` "Model properties" | 기본 생성 설정(temperature, top_p, top_k, min_p, repeat_penalty, presence/frequency_penalty, repeat_last_n, n_predict), 컨텍스트, 슬롯, 모델 파일 **이름만**, 엔진 빌드 | API 키 |
| `GET /v1/models` | `docs/DETAILS.md` | 모델 ID와 별칭 | API 키 |

## 사용하지 않는 것

- `GET /status`: 문서와 코드(`serve/server.py`, issue #212 주석)에 따르면 마지막 답변의 끝부분을 포함할 수 있어 호출하지 않습니다.
- 텔레메트리 수집에서는 `POST` 전체(추론, `/slots`, `/v1/load`, `/v1/unload`, `/v1/vram` 등), 관리 브리지(`python -m strata_control.management`), Strata 파일·설정·프로세스.
- `/props`의 `chat_template`, 모델 파일 전체 경로(사용자 이름이 들어갈 수 있음).

## 클라이언트 규칙 (Strata 자체 모니터 클라이언트 `strata_cli/client.py`와 동일)

- 서버 루트 URL만 허용, URL에 자격 증명 금지, 원격은 https만.
- 리디렉션을 따르지 않음, 응답 2 MiB 제한, `application/json`만 허용.
- 키는 `Authorization: Bearer`로만 보내고 URL·명령줄에 넣지 않음. `/health`에는 키를 보내지 않음.
- 서버 데이터는 신뢰하지 않음: 타입이 틀리면 null, 숫자는 유한값만, 문자열은 제어 문자 제거·길이 제한, 목록 개수 제한, activity 이벤트는 순서·형식 검증.
- `null`은 0이 아니라 "값 없음"으로 유지합니다. 전체 출력 처리량은 같은 인스턴스의 카운터 두 번 사이에서만 계산합니다(계산값 표시).

## 오류 분류

| 상황 | 분류 | 화면 상태 | 재시도 |
|---|---|---|---|
| 연결 거부, 이름 해석 실패 | 연결 오류 | 연결 안 됨 | 1→2→4…30초 |
| 제한 시간 초과 | 시간 초과 | 응답 시간 초과 | 지수 백오프 |
| 401/403 | 인증 오류 | 인증 필요 | 30초 |
| `schema_version` ≠ 1, JSON 아님, 형식 오류, 알 수 없는 4xx, 알려진 엔드포인트 없음, 다른 서비스 | **API 호환성 오류** | API 호환성 오류 | 30초 |
| 5xx | 서버 오류 | 서버 오류 | 지수 백오프 |
| 3xx, TLS 오류 | 설정 오류 | 설정 오류 | 30초 |

## Strata 데스크톱 앱과의 관계 (실기 확인, 2026-11-01)

사용자 PC(Strata 앱 0.2.x)에서 확인한 내용과 소스 근거입니다.

- 엔진(`serve/server.py`)이 `127.0.0.1:18500`에서 `/health`에 `"service": "strata"`, `"api_key": true`로 응답했습니다.
- 앱의 Serving API 키로 `/v1/telemetry`를 요청하면 401이 나왔습니다. curl로 직접 요청해도 같았습니다.
- 소스 근거: `apps/host/server/native-control.mjs`의 `loadEngine()`은 모델을 로드할 때마다 `randomBytes(32)`로 엔진 키를, `freePort()`로 포트를 새로 만듭니다.
  앱 호스트(47831)는 `/v1/models`와 추론만 공개하고 `/v1/telemetry`는 공개하지 않습니다.
- 결론: 데스크톱 앱으로 실행한 엔진의 텔레메트리는 공개 인터페이스로 읽을 수 없습니다. 내부 파일(`native-instances/*.json`)에서 키를 읽는 것은 비공개 인터페이스라 하지 않습니다.
  `strata launch`(Terminal)로 실행한 엔진은 문서화된 경로(`--port`, `--show-key`, `STRATA_TELEMETRY_KEY`)로 연결할 수 있습니다.

## 추론 측정 (사용자가 켜는 기능, POST 1종)

데스크톱 앱 엔진의 텔레메트리를 읽을 수 없으므로, 앱 호스트가 공개하는 추론 API로 성능을 직접 잽니다.
Strata 앱의 "Strata를 앱에 연결" 화면이 안내하는 주소(`http://127.0.0.1:47831/v1`)와 Serving API 키를 씁니다.

| 항목 | 내용 |
|---|---|
| 보내는 요청 | `GET /v1/models`(모델 ID를 비워 둔 경우만), `POST /v1/chat/completions` |
| 요청 본문 | 고정 시험 문장 1개 `Explain how a rainbow forms in clear, simple language.`(ID `lapc-rainbow-v1`), `temperature 0`, `reasoning_effort "none"`, `max_tokens 200`(설정 가능), `stream true`, `stream_options.include_usage true`. Strata 데스크톱 앱 자체 벤치마크와 같은 문장·설정입니다 |
| 언제 보내나 | [지금 측정] 버튼, `lapc bench`, 또는 사용자가 켠 자동 측정(기본 꺼짐, N분마다 1회, 최근 15초 GPU 사용률이 기준 이상이면 미룸) |
| 앱이 재는 값 (실측) | 첫 토큰까지 시간: 요청 전송부터 내용(`content` 또는 `reasoning_content`)이 있는 첫 청크 수신까지 |
| 앱이 계산하는 값 | 생성 속도 = (출력 토큰 − 1) ÷ 첫 토큰~마지막 토큰 수신 시간. 출력 토큰이 2개 미만이면 N/A |
| 엔진 보고값 | 마지막 청크의 `usage`(prompt/completion/cached 토큰), `timings.predicted_per_second`, `timings.prompt_per_second` |
| 저장하지 않는 것 | 응답 내용(시간 측정에만 쓰고 버림), API 키 값 |
| 영향 | 측정 중 GPU를 잠시 씁니다. 엔진의 프롬프트 캐시가 바뀌어 진행 중인 대화의 다음 응답이 조금 늦어질 수 있습니다 |

## 로컬 공유(LAN 공유)를 쓰지 않는 이유

Strata 앱의 "로컬 공유"(`apps/host/server/local-share.mjs`)는 `0.0.0.0:8792`에서 `/v1/models`, `/v1/chat/completions`, `/v1/messages`만
허용하고 나머지는 404를 돌려줍니다. 텔레메트리는 여기서도 공개되지 않고, 47831에서 이미 쓸 수 있는 API와 같습니다.
켜면 같은 네트워크의 다른 장치에 HTTP로 모델이 공개되므로, 이 프로그램은 로컬 공유를 요구하지 않습니다.

