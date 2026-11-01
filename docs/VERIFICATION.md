# 검증 기록

이 문서는 실제로 실행한 검증만 적습니다. 실행하지 않은 항목은 "미검증"으로 따로 둡니다.

## 1. 개발 환경(Linux 컨테이너)에서 실행한 것

환경: Ubuntu 24.04, .NET SDK 10.0.114, NVIDIA GPU 없음.

| 항목 | 결과 |
|---|---|
| 전체 솔루션 빌드 (WPF 포함, `EnableWindowsTargeting`) | 통과 |
| 자동 테스트 (`dotnet test`) | 통과 (CI 결과 섹션 참고) |
| CLI 종단 간: 실제 Linux CPU/RAM + 가상 Strata로 기록 2회, 비교, CSV/JSON 내보내기, 백업, 무결성 검사 (`scripts/smoke-cli.ps1`) | 통과 |
| NVML 없음 처리 (GPU 항목 N/A와 사유 표시) | 확인 |

## 2. Windows CI (GitHub Actions `windows-latest`)

결과는 PR의 CI 실행 기록과 아티팩트(`LocalAiPerformanceCenter-win-x64`, `smoke-test-evidence`)에 있습니다.

## 3. 미검증 (실기 필요)

- **RTX 5080 실기에서의 NVML 값**(사용률·온도·전력·VRAM·클럭·PCIe·에너지 카운터·클럭 제한 사유). CI 가상 머신에는 NVIDIA GPU가 없습니다.
- **실제 Strata 서버와의 연결**. 테스트는 Strata 공개 문서의 응답 형식을 따른 가상 서버로만 했습니다.
- Ryzen 7 9800X3D에서 CPU 사용률이 작업 관리자와 얼마나 일치하는지(`GetSystemTimes` 기준, 작업 관리자는 "Processor Utility" 사용).
- 장시간(수일) 기록 시 DB 크기·보존 정책 동작, 절전/재개 후 공백 기록.
- 고 DPI·다중 모니터에서의 화면 배치.
