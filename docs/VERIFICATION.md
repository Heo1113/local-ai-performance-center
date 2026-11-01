# 검증 기록

이 문서는 실제로 실행한 검증만 적습니다. 실행하지 않은 항목은 "미검증"으로 따로 둡니다.

## 1. 개발 환경(Linux 컨테이너)에서 실행한 것

환경: Ubuntu 24.04, .NET SDK 10.0.114, NVIDIA GPU 없음.

| 항목 | 결과 |
|---|---|
| 전체 솔루션 빌드 (WPF 포함, `EnableWindowsTargeting`) | 통과 |
| 자동 테스트 (`dotnet test`, xUnit v3) | 82개 전부 통과 (단위·통합·회귀·예외 상황) |
| Windows x64 단일 EXE 교차 게시 (`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`) | 성공, 약 77 MB. Windows에서 실행해 보지는 못함 |
| XAML 바인딩 이름과 뷰모델 속성 정적 대조 | 불일치 없음 |
| 그래프 렌더링: 앱과 같은 렌더러(`ChartRenderer`)로 모든 차트 모델을 PNG로 그려 육안 확인 (실시간 9종, 기록 1종, 비교 1종) | 확인. 반복 렌더링 시 범례 패널이 쌓이던 결함을 발견해 수정하고 테스트로 고정 |
| CLI 종단 간: 실제 Linux CPU/RAM + 가상 Strata로 기록 2회, 비교, CSV/JSON 내보내기, 백업, 무결성 검사 (`scripts/smoke-cli.ps1`) | 통과 |
| NVML 없음 처리 (GPU 항목 N/A와 사유 표시) | 확인 |

### 자동 테스트가 다루는 범위

| 영역 | 내용 |
|---|---|
| Strata 어댑터 | schema v1, 구버전(`/metrics`+`/v1/status`) 폴백, status-only, 스키마 버전 변경·비JSON·깨진 JSON(호환성 오류), 500(서버 오류), 다른 서비스, 인증 필요/키 사용, 시간 초과, 미실행(연결 오류)과 백오프, GET·허용 엔드포인트만 호출·`/status` 미호출, `/health`에 키 미전송, 원격 http 거부 |
| 파서 회귀 | Strata 문서 형식 픽스처(telemetry v1, legacy metrics, status, props): 단위·null 유지, 잘못된 타입→null, 제어 문자 제거, activity 검증, 모델 파일 경로 제거 |
| 저장소 | 왕복 기록, 중복 요청 무시, 비정상 종료 복사본 복구, 손상 파일 격리, 새 버전 DB 비변경, 기록 중 세션 삭제 금지, 보존 기간·용량 상한, 백업·검증·복원, 잘못된 백업 거부 |
| 기록기 | 세션 밖 프레임 무시, 모델 변경 시 자동 세션 분할, 수동 세션은 분할 없이 이벤트, 세션 이전 요청 제외, 연결 상태·수집 공백 이벤트 |
| 분석 | 통계 수치, 추세, 성능 저하 후보와 동시 관측, 짧은 하락 무시, 에너지/토큰, 비교 변화율·개선/저하 방향·조건 차이 경고 |
| 내보내기 | CSV 이스케이프·수식 주입 방지·BOM, JSON 구조, 진단 묶음에 키 값 미포함 |
| 뷰모델 | 실제 MonitoringService(가상 센서+가상 Strata)로 6개 화면 흐름: 대시보드 갱신, 하드웨어 표, 런타임 표, 수동 기록 2회, 기록 조회·CSV 내보내기, 비교(색 유지), 설정 검증·저장·적용 |

## 2. Windows CI (GitHub Actions `windows-latest`)

실행: [CI #3](https://github.com/Heo1113/local-ai-performance-center/actions/runs/41314333713), 커밋 `f702c08`, 2026-11-01. 러너: AMD EPYC 가상 머신, NVIDIA GPU 없음.

| 단계 | 결과 |
|---|---|
| 빌드 (Windows, Release) | 성공 |
| 자동 테스트 | 86개 전부 통과 |
| CLI 실측 스모크: Windows CPU/RAM API + 가상 Strata, 기록 2회·비교·내보내기·백업·무결성 검사 | 통과 (시스템 샘플 16, CPU 16, RAM 16, Strata 연결 샘플 15, GPU 0 = NVML 없음) |
| 포터블 패키지 (자체 포함 단일 EXE) | 생성, zip 68.1 MB, SHA-256 파일 동봉. 아티팩트 `LocalAiPerformanceCenter-win-x64` |
| GUI 스모크 (게시된 EXE, 실제 센서) | 통과: 프레임 19, 6개 화면 이동, 수동 기록 2회, 기록 통계, 비교, 설정, 예외 0, 바인딩 오류·경고 0. GPU는 "nvml.dll을 찾을 수 없습니다"로 N/A 표시 |
| GUI 스모크 (게시된 EXE, 가상 센서) | 통과: 같은 검사, GPU 그래프 경로 포함. 화면별 스크린샷은 아티팩트 `smoke-test-evidence` |

처음 실행(#1)에서 Windows 전용 결함을 발견해 수정했습니다: Windows는 닫힌 포트 연결을 약 2초 재시도해서
Strata 미실행이 "응답 시간 초과"로 잘못 분류되었습니다. TCP 연결 단계에 별도 제한 시간을 두어 "연결 오류"로 분류하도록 고쳤습니다.

## 3. 미검증 (실기 필요)

- 화면의 시각적 품질(배치, 다크 테마 적용 상태): CI가 스크린샷을 남기지만 이 작업 환경에서는 아티팩트를 열 수 없어 직접 보지 못했습니다. 실행·바인딩·화면 전환은 CI에서 확인했습니다.
- 파일 대화 상자(백업 경로 선택 등)의 실제 조작: 스모크 테스트는 대화 상자를 띄우지 않는 무인 모드로 실행합니다.

- **RTX 5080 실기에서의 NVML 값**(사용률·온도·전력·VRAM·클럭·PCIe·에너지 카운터·클럭 제한 사유). CI 가상 머신에는 NVIDIA GPU가 없습니다.
- **실제 Strata 서버와의 연결**. 테스트는 Strata 공개 문서의 응답 형식을 따른 가상 서버로만 했습니다.
- Ryzen 7 9800X3D에서 CPU 사용률이 작업 관리자와 얼마나 일치하는지(`GetSystemTimes` 기준, 작업 관리자는 "Processor Utility" 사용).
- 장시간(수일) 기록 시 DB 크기·보존 정책 동작, 절전/재개 후 공백 기록.
- 고 DPI·다중 모니터에서의 화면 배치.
