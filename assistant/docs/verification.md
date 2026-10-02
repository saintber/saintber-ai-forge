# 規格情境與驗證證據

本文件是 spectra-apply 的結尾測試對照，不是完整 spectra-verify / review 報告。來源為 openspec/changes/assistant-line-connector/specs/ 的四份 delta specs；逐項檢查 207 個 Scenario 與 3 個 Example。coverage 指已檢查的測試斷言，與執行是否通過分開。手動命令且有核對預期結果的容器測試也列為測試；只閱讀程式碼不算執行通過。

## 分類

| 結果 | 項數 |
| --- | ---: |
| 有測試 | 193 |
| 依測試範圍排除 | 4 |
| 未完整涵蓋 | 13 |

未完整涵蓋包含部分已有測試、但仍缺指定時序、輸出或跨層斷言的情境；不能把它們稱為完全驗收。排除只適用文件內容，不排除條件、預設或錯誤退化路徑。

## 執行結果

| 狀態 | 命令／範圍 | 結果與來源 |
| --- | --- | --- |
| passed-current | dotnet build assistant/Assistant.sln -c Release | 2026-10-02 本次命令輸出：0 warning、0 error |
| passed-current | dotnet test assistant/Assistant.sln -c Release --no-build --no-restore | 本次：Abstractions 4、Core 98、LINE 81、Host 130 通過；Host 1 Windows file symlink 略過；總計 313 通過 / 1 略過 |
| passed-current | npm test --prefix assistant/tests/Assistant.Ci.Tests | 本次 3/3；另有放寬 paths、移除 needs、移除 Podman 的測試突變失敗證據，還原後通過 |
| passed-current | npm pack --dry-run --json | 本次篩選 assistant/*：0 entries；根 package.json 未變更 |
| passed-current | Podman build、compose up、health、uid、LINE 載入、sentinel、兩種 compose stop | 本次輸出；完整重現與數值見 container-verification.md |
| passed-prior | Linux SDK container 中 HostLoadingTests | 前段本次工作記錄：19/19、無 skip；loader、HostLoadingTests、publish fixture 未再修改，Windows file-link 略過已有 Linux 證據；不是整個 Linux solution 通過宣告 |
| not-run | Docker、GitHub Actions、真實 LINE 與流量／預算調校 | 本機無 Docker；沒有推送分支或真實 LINE 憑證，需 tasks.md 7.1 使用者回報 |

工具測試與文件實走結果待 task 15 完成後追加。

## 範例值

- TTL 表兩列 599 / 601 秒與 600 秒 TTL 出現在 EventRegistryTests.Ttl_example_boundaries，兩列都有 TryAdmit 斷言；尚未驗真正 handler 再次呼叫，因此 Example 列為缺口。
- 獨立 HMAC 範例：test-secret、原始 body {"destination":"U0","events":[]}、固定簽章 AQQTPDN0VEjXZIlgGdat3T+SL3wHGAG+cHc684p/XqU=，成功與末字元變動失敗由 LineWebhookInboundTests 驗證。
- Sentinel 範例：SENTINEL-7F3A-NOT-A-SECRET 寫入 data/assistant.env 與 assistant/leak.env；最終 archive 234464256 bytes、build-stage archive 934889984 bytes 都搜尋 0 筆。Docker 執行仍 not-run。

## 測試索引

測試檔均在 assistant/tests/ 各同名專案；Core / LINE / Host 方法為已閱讀的斷言索引。每個索引對應下表同一 Requirement 的情境，缺少完整斷言者另標缺口。

### F1

connector-framework / Platform adapter contract

CapabilityTests.Unsupported_activity_and_reply_are_never_invoked; PipelineTests.Empty_parse_returns_success_without_work

### F2

connector-framework / Webhook connector pipeline

AdditionalScenarioTests.Three_events_are_admitted_in_original_order; PipelineTests.Verification_and_payload_errors_do_not_admit / Four_workers_and_two_pending_only_admit_first_two_after_parse_cancellation / Worker_waits_on_registration_gate

### F3

connector-framework / Early acknowledgement and background event work

PipelineTests.Early_ack_before_three_second_activity_and_five_second_handler_and_request_abort / Failure_isolated_no_retries_and_sensitive_logs_excluded / Ten_events_never_exceed_four_handlers / Four_workers_and_two_pending_only_admit_first_two_after_parse_cancellation / Queue_age_retains_registration_and_budget_starts_with_worker

### F4

connector-framework / Redelivery deduplication

EventRegistryTests.Ttl_example_boundaries / Concurrent_duplicates_only_enqueue_once_and_other_ids_are_independent / Oldest_eviction_and_failed_enqueue_do_not_evict_or_register / Twenty_thousand_unique_events_stay_bounded_and_background_sweep_clears / Invalid_ttl_names_setting; PipelineTests.Concurrent_duplicates_call_handler_once; AdditionalScenarioTests.Evicted_event_can_run_again_while_original_handler_is_still_running

### F5

connector-framework / Reply context and delivery routing

ReplyActivityTests.Reply_uses_conservative_event_time_and_local_expiry / Mismatched_chat_pushes_without_consuming_and_logs / Shared_context_parallel_consumption_and_retained_reference_survive_eviction / Missing_in_reply_to_pushes_and_unavailable_routes_are_logged; AdditionalScenarioTests.Expired_reply_without_push_is_logged_as_undeliverable; LineWebhookInboundTests.Keys_and_sanitized_metadata_survive_released_source; HostLinePublishTests.Clean_published_line_instances_keep_dedup_reply_and_stop_state_independent

### F6

connector-framework / Activity indicator lifecycle

ReplyActivityTests.Activity_disposed_once_by_reply_or_duration / Late_activity_is_disposed_without_registration_and_repeating_timer_stops / Immediate_activity_failure_calls_handler_once_and_records_message_key / Activity_failure_and_late_failure_are_observed_without_sensitive_text; PipelineTests.Early_ack_before_three_second_activity_and_five_second_handler_and_request_abort

### F7

connector-framework / Timeouts and event work budget

FrameworkSettingsTests.Activity_timeout_continues_to_handler_and_event_budget_cancels_cooperative_work / Hanging_reply_is_cancelled_and_does_not_block_other_events / Hanging_send_is_cancelled_then_next_call_is_unaffected

### F8

connector-framework / Worker overrun and degraded admission

LeaseOverrunTests.One_overrun_worker_does_not_block_other_three_and_logs_error_once / All_four_overrun_drop_without_registration_then_recover_and_replay / Recovery_discards_forty_second_queue_item_and_processes_ten_second_item / Degraded_return_during_stopping_or_after_stopped_does_not_recover / Overrun_handler_cannot_deliver_before_stopping

### F9

connector-framework / Delivery acceptance and lease classification

LeaseOverrunTests.Child_delivery_after_return_or_budget_expiry_has_revoked_lease / Valid_lease_TaskRun_delivery_is_accepted_in_running_and_stopping / No_lease_push_and_platform_failure_keep_accepted_result / Another_instance_lease_is_only_a_no_lease_delivery / Lease_is_not_exposed_to_host_visible_contracts; HostLinePublishTests.Lease_from_A_cannot_authorize_delivery_to_stopping_B; StopCleanupTests.Stopped_delivery_is_rejected_without_creating_any_new_timer

### F10

connector-framework / Connector stop and cleanup

StopCleanupTests.Stop_drops_three_pending_with_ids_keeps_registration_and_waits_for_existing_delivery / Grace_cancellation_joins_two_hundred_millisecond_cooperative_cleanup / Noncooperative_handler_is_abandoned_at_join_and_late_send_is_unavailable / Independent_send_is_cancelled_and_joined_until_original_task_settles / Host_cancellation_at_two_seconds_returns_without_joining_noncooperative_work_or_send / Late_activity_after_stopped_is_disposed_once_without_handler_or_activity_registration / Admission_and_stop_are_fully_ordered_by_same_gate / Two_activities_are_disposed_once_and_clean_stop_leaves_no_timer_callbacks / Stop_budget_is_grace_plus_join

### F11

connector-framework / Framework settings schema

FrameworkSettingsTests.Descriptors_declare_all_defaults_and_bounds / Invalid_values_name_only_key; AdditionalScenarioTests.Settings_override_runs_eight_workers_and_cancels_at_sixty_seconds; FactoryTests.Schema_includes_required_secret_defaults_and_every_core_descriptor

### F12

connector-framework / Best-effort delivery

FrameworkSettingsTests.Hanging_reply_is_cancelled_and_does_not_block_other_events; LeaseOverrunTests.Synchronous_platform_failure_keeps_accepted_and_ends_reply_activity; PipelineTests.Failure_isolated_no_retries_and_sensitive_logs_excluded

### F13

connector-framework / Framework logging hygiene

LeaseOverrunTests.No_lease_push_and_platform_failure_keep_accepted_result; ReplyActivityTests.Immediate_activity_failure_calls_handler_once_and_records_message_key; PipelineTests.Failure_isolated_no_retries_and_sensitive_logs_excluded

### H1

assistant-host / Connector loading from configuration

HostLoadingTests.Clean_publish_same_path_factory_once_shared_type_and_independent_instances / Different_paths_have_different_contexts / Parent_links_resolve_to_same_context_and_escapes_are_refused / Missing_and_escaped_path_fail_safely / Wrong_major_fails_before_loading_with_both_versions / Invalid_id_fails; HostLinePublishTests.Clean_published_line_instances_keep_dedup_reply_and_stop_state_independent; HostHealthTests.Composition_starts_with_registered_handler_and_sets_budget_then_stops_once

### H2

assistant-host / Webhook route

HostWebhookTests.Real_line_echo_routes_and_user_loading_order / Bad_signature_or_malformed_json_is_rejected / Unknown_disabled_or_non_webhook_instance_returns_404 / Body_limit_applies_to_known_and_streamed_lengths_before_connector / Route_forwards_original_bytes_headers_and_status

### H3

assistant-host / Inbound message handler seam

HostLoadingTests.Clean_publish_same_path_factory_once_shared_type_and_independent_instances; HostMessagingTests.Cancelled_handler_sends_nothing; FrameworkSettingsTests.Activity_timeout_continues_to_handler_and_event_budget_cancels_cooperative_work

### H4

assistant-host / Outbound gateway

HostMessagingTests.Gateway_routes_only_to_matching_type_and_instance / Unknown_or_disabled_target_calls_no_connector; HostWebhookTests.Gateway_remains_accepted_on_platform_failure_or_transport_exception / Stopping_and_stopped_return_unavailable_and_webhook_503_without_platform

### H5

assistant-host / Echo handler

HostMessagingTests.Echo_preserves_target_and_text_with_mode_specific_reply / Echo_preserves_all_five_thousand_input_units / Invalid_echo_mode_names_only_the_setting; HostWebhookTests.Real_line_echo_routes_and_user_loading_order / Line_truncates_the_echo_of_five_thousand_characters; HostStartupProcessTests.Invalid_echo_mode_exits_nonzero_without_printing_the_mode_value

### H6

assistant-host / Connector instance list and settings schema

HostConfigurationTests.Common_fields_real_line_defaults_and_summary_never_include_setting_values / Misspelled_common_fields_and_scalar_shapes_fail_without_values / Object_common_fields_fail / Nested_json_and_environment_really_run_eight_workers_with_sixty_second_budgets / Unknown_kind_range_and_duration_fail_before_factory_without_values / Branch_and_leaf_conflict_fails_with_branch_key / Ids_and_settings_are_case_insensitive_and_canonicalized / Seven_passes_host_range_but_fails_real_factory_platform_rule / Disabled_instance_does_not_load_and_logs_only_summary / Json_external_environment_precedence_is_eight_then_six_then_four / Merge_by_id_preserves_omitted_instances_and_secrets_follow_id_after_reordering / Environment_fragment_without_type_fails_instead_of_silently_ignoring_it / Explicit_external_file_failures_are_sanitized / Shipped_settings_keep_line_disabled_and_health_available_without_secrets; HostWebhookTests.No_configuration_listing_endpoint_exists

### H7

assistant-host / Configuration and secret handling

HostConfigurationTests.Missing_required_secret_never_prints_another_secret / Common_fields_real_line_defaults_and_summary_never_include_setting_values / Environment_only_configuration_uses_the_real_provider_and_creates_line; HostStartupProcessTests.Missing_access_token_exits_nonzero_without_printing_the_present_secret / Unknown_setting_exits_nonzero_without_printing_any_setting_values

### H8

assistant-host / Parallel connector shutdown and shutdown budget

HostLifecycleTests.Three_eight_second_stops_run_in_eight_seconds_once / One_throw_does_not_stop_other_cleanup_and_logs_only_its_id / Fifteen_and_twenty_five_with_default_margin_sets_thirty_and_logs_it / Invalid_margin_fails_without_values / Host_token_is_passed_and_interrupts_immediately; HostLoadingTests.Host_has_no_connector_or_core_references

### H9

assistant-host / Health endpoint

HostHealthTests.Health_is_fixed_200_without_configuration_or_external_calls

### H10

assistant-host / Dependency direction

Host.Tests/UnitTest1.Project_dependencies_follow_contract / Compiled_dependencies_follow_contract / Compilable_fixtures_detect_forbidden_project_and_package; Abstractions.Tests/AssemblyBoundaryTests, Core.Tests/AssemblyBoundaryTests, Line.Tests/AssemblyBoundaryTests

### L1

line-connector / Webhook signature verification

LineWebhookInboundTests.Independent_signature_vector_verifies / One_changed_body_byte_is_rejected / Missing_invalid_or_changed_signature_is_rejected / Raw_non_ascii_and_escaped_bytes_verify_with_case_insensitive_header

### L2

line-connector / Text message event parsing

LineWebhookInboundTests.Text_event_carries_platform_fields_and_matching_stable_keys / Multiple_events_keep_original_order / Skipped_events_only_log_types_and_do_not_hide_later_text / Malformed_payload_is_connector_payload_error / Invalid_eligible_text_payload_is_connector_payload_error

### L3

line-connector / Deterministic external keys

LineWebhookInboundTests.Text_event_carries_platform_fields_and_matching_stable_keys / Keys_and_sanitized_metadata_survive_released_source / Keys_do_not_depend_on_instance_event_or_json_property_order; ExternalKeyTests

### L4

line-connector / Sanitized raw metadata

LineWebhookInboundTests.Keys_and_sanitized_metadata_survive_released_source

### L5

line-connector / Reply message sending

LineOutboundTests.Reply_uses_post_bearer_and_one_text_message / Http_400_errors_contain_only_status_without_response_or_sensitive_values; HostWebhookTests.Gateway_remains_accepted_on_platform_failure_or_transport_exception

### L6

line-connector / Push message sending

LineOutboundTests.Push_uses_the_chat_properties_destination / Invalid_chat_is_undeliverable_without_http_or_identifier_disclosure

### L7

line-connector / Text length limit on UTF-16 boundary

LineOutboundTests.Text_length_is_limited_to_five_thousand_utf16_units / Emoji_crossing_the_limit_is_removed_as_a_complete_pair / Exactly_five_thousand_units_and_complete_emoji_at_boundary_are_unchanged

### L8

line-connector / Loading indicator for one-to-one chats

LineOutboundTests.User_loading_request_uses_configured_seconds_and_returns_noop_disposable / Group_and_room_activity_return_null_without_request / Late_loading_result_can_be_disposed_without_any_http_action; FactoryTests.Loading_validation_names_key_without_value

### L9

line-connector / LINE capability declaration and settings

LineOutboundTests.Platform_declares_all_capabilities_and_delegates_signature_and_payload_to_existing_parser; FactoryTests.Schema_includes_required_secret_defaults_and_every_core_descriptor / Missing_or_empty_secret_fails_with_key_only_before_creating_client / Defaults_compose_webhook_platform_and_infinite_http_timeout_with_matching_context

### L10

line-connector / Sensitive data is not disclosed

LineOutboundTests.Platform_declares_all_capabilities_and_delegates_signature_and_payload_to_existing_parser（SUPPLIED-SIGNATURE-SECRET / BODY-SECRET 錯誤與日誌均不含 SECRET）

### C1

assistant-container-delivery / Engine-neutral container build

container-verification.md 建置、uid、DLL 與 healthz 步驟；Podman 本機通過，Docker not-run

### C2

assistant-container-delivery / Build context isolation

container-verification.md sentinel 全層搜尋：最終 archive 234464256 bytes、build-stage archive 934889984 bytes，字串 SENTINEL-7F3A-NOT-A-SECRET 均 0 筆（Podman）；Docker not-run

### C3

assistant-container-delivery / Compose definition

container-verification.md 實際 compose up/stop：healthz 200、cooperative cleanup completed 與 uncooperative join abandoned，兩者 ExitCode 0；靜態 compose 無 socket/home volume

### C4

assistant-container-delivery / Secret files are not committed

git status --short -- data/assistant.env 空輸出；git check-ignore 顯示 data/assistant.env；assistant/.env.example 人工檢查僅占位值

### C5

assistant-container-delivery / Installation and run documentation

人工審閱 install-container.md / settings-reference.md / manual-test-line.md / architecture.md 及 Podman 文件實走；實際 LINE/Docker not-run

### C6

assistant-container-delivery / Offline fake webhook tool

scripts/fake-webhook-tests.cs；實際 localhost:8080 正確 secret→200、錯誤→401、假 LINE API reply（待 agent 最終證據）

### C7

assistant-container-delivery / Continuous integration

tests/Assistant.Ci.Tests/assistant-ci.test.mjs 三個 node:test，YAML AST 驗 paths / 成功相依 / 兩 engine / 無 push；遠端 Actions not-run

### C8

assistant-container-delivery / Independence from the saifg package

npm pack --dry-run 舊記錄；package.json 未修改；Project_dependencies_follow_contract

## connector-framework

| 類型與情境 | 分類 | 對照／缺口或排除理由 |
| --- | --- | --- |
| Scenario: Unsupported capability is not called | 有測試 | 測試索引 [F1](#f1) |
| Scenario: Platform declares no activity support | 有測試 | 測試索引 [F1](#f1) |
| Scenario: Events the platform skips | 有測試 | 測試索引 [F1](#f1) |
| Scenario: Events admitted in order | 有測試 | 測試索引 [F2](#f2) |
| Scenario: Verification failure | 缺口 | 測試只驗 401 與 handler 未呼叫，沒有驗 Parse 呼叫數為 0。補 counting inbound。 |
| Scenario: Payload failure | 有測試 | 測試索引 [F2](#f2) |
| Scenario: Request cancelled after parsing | 有測試 | 測試索引 [F2](#f2) |
| Scenario: Work cannot start before registration commits | 有測試 | 測試索引 [F2](#f2) |
| Scenario: Response does not wait for processing | 有測試 | 測試索引 [F3](#f3) |
| Scenario: Request abort does not cancel work | 有測試 | 測試索引 [F3](#f3) |
| Scenario: Event failure is isolated | 有測試 | 測試索引 [F3](#f3) |
| Scenario: Concurrency limit | 有測試 | 測試索引 [F3](#f3) |
| Scenario: Overload discards without registering | 有測試 | 測試索引 [F3](#f3) |
| Scenario: Redelivery of an overload-discarded event | 有測試 | 測試索引 [F3](#f3) |
| Scenario: Queue age exceeded | 缺口 | 31 秒測試驗丟棄與重送，但 fake 未宣告 Activity 且未斷言 queue-age 日誌。補 Activity-capable fake 與日誌斷言。 |
| Scenario: Budget starts when processing starts | 有測試 | 測試索引 [F3](#f3) |
| Scenario: Redelivered event is discarded | 有測試 | 測試索引 [F4](#f4) |
| Scenario: Concurrent duplicates | 有測試 | 測試索引 [F4](#f4) |
| Scenario: Different events are independent | 有測試 | 測試索引 [F4](#f4) |
| Scenario: Expiry after the time-to-live | 缺口 | 現有 registry 測試驗 TryAdmit（599/601 秒）；尚未以真正 worker 斷言 handler 再次呼叫。補 webhook→handler 的同值 TTL 整合測試。 |
| Example: time-to-live boundaries | 缺口 | 現有 registry 測試驗 TryAdmit（599/601 秒）；尚未以真正 worker 斷言 handler 再次呼叫。補 webhook→handler 的同值 TTL 整合測試。 |
| Scenario: Eviction ends protection early | 有測試 | 測試索引 [F4](#f4) |
| Scenario: Eviction while the first processing still runs | 有測試 | 測試索引 [F4](#f4) |
| Scenario: Registry stays bounded | 有測試 | 測試索引 [F4](#f4) |
| Scenario: Invalid time-to-live | 有測試 | 測試索引 [F4](#f4) |
| Scenario: Fresh token uses reply | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Locally expired token uses push | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Late event time shortens validity | 有測試 | 測試索引 [F5](#f5) |
| Scenario: No in-reply-to uses push | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Token is single use | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Parallel outbound consume the token once | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Chat mismatch with a valid token | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Chat mismatch with an expired token | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Two registrations share one context | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Used state survives while any registration remains | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Used state ends with the last registration | 缺口 | 共享 context 測試驗第二次 reply 失敗不 push，但未斷言 rejection 日誌。補對应失敗日誌。 |
| Scenario: Platform rejection does not fall back to push | 缺口 | 共享 context 測試驗第二次 reply 失敗不 push，但未斷言 rejection 日誌。補對应失敗日誌。 |
| Scenario: Neither route available | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Usable redelivered token is still pushed when locally expired | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Token never reaches the host | 有測試 | 測試索引 [F5](#f5) |
| Scenario: Indicator started before handler | 有測試 | 測試索引 [F6](#f6) |
| Scenario: Indicator stopped on reply | 有測試 | 測試索引 [F6](#f6) |
| Scenario: Indicator stopped on timeout | 有測試 | 測試索引 [F6](#f6) |
| Scenario: Indicator failure | 有測試 | 測試索引 [F6](#f6) |
| Scenario: Platform declines the indicator | 缺口 | null result 有正常完成 handler 證據，但未直接斷言沒有活動登記與錯誤日誌。補 null 退化路徑斷言。 |
| Scenario: Late result while running | 缺口 | 晚到測試在 2 秒後立即送回覆，未精確推進至 2.1 秒，也未斷言 Running 的 _activities/新增 timer 為 0；Stopped 情境另有完整斷言。補此 Running 時序。 |
| Scenario: Late result with a local timer | 有測試 | 測試索引 [F6](#f6) |
| Scenario: Late failure is observed | 有測試 | 測試索引 [F6](#f6) |
| Scenario: Activity call hangs | 有測試 | 測試索引 [F7](#f7) |
| Scenario: Reply call hangs | 缺口 | reply hang 測試只有一個事件，沒有真的處理下一事件或斷言 timeout 日誌。補第二事件與日誌。 |
| Scenario: Event budget exhausted | 缺口 | 現有 cooperative handler 自行吞取消，測試驗取消返回；未斷言 failure 日誌與其他事件不受影響。補事件預算失敗後下一事件。 |
| Scenario: One handler overruns | 有測試 | 測試索引 [F8](#f8) |
| Scenario: All handlers overrun | 有測試 | 測試索引 [F8](#f8) |
| Scenario: Recovery while running | 有測試 | 測試索引 [F8](#f8) |
| Scenario: Queued work after recovery | 有測試 | 測試索引 [F8](#f8) |
| Scenario: Handler returns during stopping | 有測試 | 測試索引 [F8](#f8) |
| Scenario: Handler returns after stopped | 有測試 | 測試索引 [F8](#f8) |
| Scenario: Overrun handler cannot send | 有測試 | 測試索引 [F8](#f8) |
| Scenario: Caller without a lease while running | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Valid lease while running | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Revoked lease after the handler returned | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Revoked lease after the budget expired | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Valid lease while stopping | 有測試 | 測試索引 [F9](#f9) |
| Scenario: No lease while stopping | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Any caller after stop | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Platform failure after acceptance | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Another instance's lease while stopping | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Another instance's lease while running | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Two instances from one assembly and from separate load contexts | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Lease stays private | 有測試 | 測試索引 [F9](#f9) |
| Scenario: Stop waits for work | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Pending work is dropped | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Stop cancels work after the grace period | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Cooperative handler finishes cleanup | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Handler ignores cancellation | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Late continuation cannot send | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Late activity result is released once | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Activity disposed on stop | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Stop budget | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Host cancels the stop | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Admission races with stop | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Nothing runs after a clean stop | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Requests after stop | 有測試 | 測試索引 [F10](#f10) |
| Scenario: Defaults | 有測試 | 測試索引 [F11](#f11) |
| Scenario: Override through settings | 有測試 | 測試索引 [F11](#f11) |
| Scenario: Invalid value | 有測試 | 測試索引 [F11](#f11) |
| Scenario: Descriptors are discoverable | 有測試 | 測試索引 [F11](#f11) |
| Scenario: Failed reply is not retried | 缺口 | 一次失敗測試涵蓋 exception/逾時；未把 server-error reply、一次呼叫及日誌放在同一跨層測試。補 fake HTTP 500 reply。 |
| Scenario: Redelivery after failure is discarded | 有測試 | 測試索引 [F12](#f12) |
| Scenario: Failure log content | 有測試 | 測試索引 [F13](#f13) |

## line-connector

| 類型與情境 | 分類 | 對照／缺口或排除理由 |
| --- | --- | --- |
| Scenario: Valid signature is accepted | 有測試 | 測試索引 [L1](#l1) |
| Scenario: Tampered body is rejected | 有測試 | 測試索引 [L1](#l1) |
| Example: known signature vector | 有測試 | 測試索引 [L1](#l1) |
| Scenario: Missing or malformed signature is rejected | 有測試 | 測試索引 [L1](#l1) |
| Scenario: Signature is computed over raw bytes | 有測試 | 測試索引 [L1](#l1) |
| Scenario: One-to-one text message | 有測試 | 測試索引 [L2](#l2) |
| Scenario: Group text message | 有測試 | 測試索引 [L2](#l2) |
| Scenario: Room text message | 有測試 | 測試索引 [L2](#l2) |
| Scenario: Multiple events in one webhook | 有測試 | 測試索引 [L2](#l2) |
| Scenario: Skipped events | 有測試 | 測試索引 [L2](#l2) |
| Scenario: Malformed payload | 有測試 | 測試索引 [L2](#l2) |
| Scenario: Canonical values | 有測試 | 測試索引 [L3](#l3) |
| Scenario: Key outlives the parser | 有測試 | 測試索引 [L3](#l3) |
| Scenario: Properties carry the original identifier | 有測試 | 測試索引 [L3](#l3) |
| Scenario: Transport fields removed | 有測試 | 測試索引 [L4](#l4) |
| Scenario: Metadata outlives the request | 有測試 | 測試索引 [L4](#l4) |
| Scenario: Reply request shape | 有測試 | 測試索引 [L5](#l5) |
| Scenario: LINE rejects the reply | 有測試 | 測試索引 [L5](#l5) |
| Scenario: Push to a user | 有測試 | 測試索引 [L6](#l6) |
| Scenario: Push to a group | 有測試 | 測試索引 [L6](#l6) |
| Scenario: Contradictory key is not sent | 有測試 | 測試索引 [L6](#l6) |
| Scenario: Plain text truncation | 有測試 | 測試索引 [L7](#l7) |
| Scenario: Emoji at the boundary | 有測試 | 測試索引 [L7](#l7) |
| Scenario: Text within the limit | 有測試 | 測試索引 [L7](#l7) |
| Scenario: Loading started for a user chat | 有測試 | 測試索引 [L8](#l8) |
| Scenario: No loading for group or room chats | 有測試 | 測試索引 [L8](#l8) |
| Scenario: Invalid loading seconds | 有測試 | 測試索引 [L8](#l8) |
| Scenario: Loading seconds out of range | 有測試 | 測試索引 [L8](#l8) |
| Scenario: Late loading result is a no-op | 有測試 | 測試索引 [L8](#l8) |
| Scenario: Capabilities | 有測試 | 測試索引 [L9](#l9) |
| Scenario: Settings schema | 有測試 | 測試索引 [L9](#l9) |
| Scenario: Missing secret | 有測試 | 測試索引 [L9](#l9) |
| Scenario: Defaults applied | 有測試 | 測試索引 [L9](#l9) |
| Scenario: Verification failure log | 有測試 | 測試索引 [L10](#l10) |

## assistant-host

| 類型與情境 | 分類 | 對照／缺口或排除理由 |
| --- | --- | --- |
| Scenario: Connector loaded from configuration | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Same path loaded once | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Instances sharing an assembly stay isolated | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Different paths use separate load contexts | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Path normalization | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Shared types | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Path escape refused | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Invalid instance id | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Missing assembly | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Loaded from a clean published folder | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Incompatible contract version | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Connector stopped on shutdown | 有測試 | 測試索引 [H1](#h1) |
| Scenario: Valid request | 有測試 | 測試索引 [H2](#h2) |
| Scenario: Bad signature | 有測試 | 測試索引 [H2](#h2) |
| Scenario: Malformed payload | 有測試 | 測試索引 [H2](#h2) |
| Scenario: Unknown instance | 有測試 | 測試索引 [H2](#h2) |
| Scenario: Disabled instance | 有測試 | 測試索引 [H2](#h2) |
| Scenario: Oversized body | 有測試 | 測試索引 [H2](#h2) |
| Scenario: Handler receives envelopes | 有測試 | 測試索引 [H3](#h3) |
| Scenario: Handler honors the deadline | 有測試 | 測試索引 [H3](#h3) |
| Scenario: Routed to the right connector | 有測試 | 測試索引 [H4](#h4) |
| Scenario: Unknown target | 有測試 | 測試索引 [H4](#h4) |
| Scenario: Delivery failure is contained | 有測試 | 測試索引 [H4](#h4) |
| Scenario: Known connector refuses while stopping | 有測試 | 測試索引 [H4](#h4) |
| Scenario: Known connector refuses after stop | 有測試 | 測試索引 [H4](#h4) |
| Scenario: Echo reply | 有測試 | 測試索引 [H5](#h5) |
| Scenario: Push mode | 有測試 | 測試索引 [H5](#h5) |
| Scenario: Invalid mode | 有測試 | 測試索引 [H5](#h5) |
| Scenario: Echo of long text | 有測試 | 測試索引 [H5](#h5) |
| Scenario: Common and custom fields | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Misspelled common field | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Wrong shape | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Nested JSON and environment variables are equivalent | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Misspelled leaf is rejected | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Branch and leaf conflict | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Keys are case-insensitive | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Wrong kind or out of range | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Duration written as a bare number | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Platform rule validated by the factory | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Disabled instance | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Secret never printed | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Precedence of sources | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Layering merges by instance id | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Reordering cannot misassign credentials | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Removal requires disable | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Fragment without a type | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Config file not set | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Config file named but missing | 有測試 | 測試索引 [H6](#h6) |
| Scenario: No listing endpoint | 有測試 | 測試索引 [H6](#h6) |
| Scenario: Missing secret | 缺口 | Host 欠 ChannelSecret 的測試驗例外，但非零子程序測試目前只刪 access token。補刪 ChannelSecret 的程序案例。 |
| Scenario: Values are never printed | 有測試 | 測試索引 [H7](#h7) |
| Scenario: Environment variable form | 有測試 | 測試索引 [H7](#h7) |
| Scenario: Instances stop in parallel | 有測試 | 測試索引 [H8](#h8) |
| Scenario: One stop fails | 有測試 | 測試索引 [H8](#h8) |
| Scenario: Shutdown budget from reported budgets | 有測試 | 測試索引 [H8](#h8) |
| Scenario: Invalid margin | 有測試 | 測試索引 [H8](#h8) |
| Scenario: Host token cancels the stop | 有測試 | 測試索引 [H8](#h8) |
| Scenario: Host does not reference the framework | 有測試 | 測試索引 [H8](#h8) |
| Scenario: Healthy | 有測試 | 測試索引 [H9](#h9) |
| Scenario: Rules hold | 有測試 | 測試索引 [H10](#h10) |
| Scenario: Forbidden project reference is detected | 有測試 | 測試索引 [H10](#h10) |
| Scenario: Forbidden package reference is detected | 有測試 | 測試索引 [H10](#h10) |
| Scenario: Host does not reference connector assemblies | 有測試 | 測試索引 [H10](#h10) |

## assistant-container-delivery

| 類型與情境 | 分類 | 對照／缺口或排除理由 |
| --- | --- | --- |
| Scenario: Build with Podman | 有測試 | 測試索引 [C1](#c1) |
| Scenario: Build with Docker | 缺口 | 本機沒有 Docker。Podman 步驟已執行，但 Docker 建置、啟動及各層 sentinel／停止仍需 task 7.1 的實際證據。 |
| Scenario: Runs as non-root | 有測試 | 測試索引 [C1](#c1) |
| Scenario: LINE connector is bundled | 有測試 | 測試索引 [C1](#c1) |
| Scenario: Sentinel outside the context | 有測試 | 測試索引 [C2](#c2) |
| Scenario: Sentinel inside the context but ignored | 有測試 | 測試索引 [C2](#c2) |
| Example: sentinel check | 有測試 | 測試索引 [C2](#c2) |
| Scenario: Start with either engine | 有測試 | 測試索引 [C3](#c3) |
| Scenario: Stop within the grace period | 有測試 | 測試索引 [C3](#c3) |
| Scenario: Uncooperative handler at stop | 有測試 | 測試索引 [C3](#c3) |
| Scenario: Compose file rules | 有測試 | 測試索引 [C3](#c3) |
| Scenario: Data directory ignored | 有測試 | 測試索引 [C4](#c4) |
| Scenario: Example file has only placeholders | 有測試 | 測試索引 [C4](#c4) |
| Scenario: Settings reference | 排除 | 排除自動化文件文字斷言：此情境檢查單一文件中可直接看見的內容，破壞可在修改處發現，沒有執行條件分支。文件提及的設定／平台／engine 行為仍由各自情境驗證或記缺口，不以此排除。 |
| Scenario: Container install guide | 排除 | 排除自動化文件文字斷言：此情境檢查單一文件中可直接看見的內容，破壞可在修改處發現，沒有執行條件分支。文件提及的設定／平台／engine 行為仍由各自情境驗證或記缺口，不以此排除。 |
| Scenario: Manual LINE test guide | 排除 | 排除自動化文件文字斷言：此情境檢查單一文件中可直接看見的內容，破壞可在修改處發現，沒有執行條件分支。文件提及的設定／平台／engine 行為仍由各自情境驗證或記缺口，不以此排除。 |
| Scenario: Architecture guide | 排除 | 排除自動化文件文字斷言：此情境檢查單一文件中可直接看見的內容，破壞可在修改處發現，沒有執行條件分支。文件提及的設定／平台／engine 行為仍由各自情境驗證或記缺口，不以此排除。 |
| Scenario: Signed fake webhook accepted | 有測試 | 測試索引 [C6](#c6) |
| Scenario: Wrong secret rejected | 有測試 | 測試索引 [C6](#c6) |
| Scenario: Pull request with a failing test | 有測試 | 測試索引 [C7](#c7) |
| Scenario: Pull request with passing tests | 有測試 | 測試索引 [C7](#c7) |
| Scenario: Unrelated change | 有測試 | 測試索引 [C7](#c7) |
| Scenario: Package contents | 有測試 | 測試索引 [C8](#c8) |

## 下一步

對標為缺口的各列補上所列出的測試；Docker／真實 LINE／Actions 依人工驗收手冊取得實際結果。Spectra apply 的此項對照不阻擋任務實作完成，但不代表所有情境都已驗收。完成 tasks.md 7.1 後再執行 spectra-verify assistant-line-connector 與 spectra-review assistant-line-connector。
