NODE ?= node

.DEFAULT_GOAL := help

.PHONY: help status check test-server test-tools test-tools-full test-unity preflight private-source merge release

COMMON_RELEASE_ARGS = \
	$(if $(strip $(DRY_RUN)),--dry-run) \
	$(if $(filter 1 true yes on,$(PUSH)),--push) \
	$(if $(filter 0 false no off,$(PUSH)),--no-push) \
	$(if $(strip $(NO_PUSH)),--no-push) \
	$(if $(strip $(TARGET_BRANCH)),--target-branch "$(TARGET_BRANCH)") \
	$(if $(strip $(TAG_PREFIX)),--tag-prefix "$(TAG_PREFIX)") \
	$(if $(strip $(LATEST_TAG)),--latest-tag "$(LATEST_TAG)")

MERGE_ARGS = \
	$(COMMON_RELEASE_ARGS) \
	$(if $(strip $(UPSTREAM_REMOTE)),--upstream-remote "$(UPSTREAM_REMOTE)") \
	$(if $(strip $(UPSTREAM_URL)),--upstream-url "$(UPSTREAM_URL)") \
	$(if $(strip $(UPSTREAM_BRANCH)),--upstream-branch "$(UPSTREAM_BRANCH)")

DEV_ARGS = \
	$(if $(strip $(DRY_RUN)),--dry-run) \
	$(if $(strip $(PRIVATE_REPO)),--private-repo "$(PRIVATE_REPO)")

UNITY_TEST_ARGS = \
	$(if $(strip $(MODE)),--mode "$(MODE)") \
	$(if $(strip $(FAILED_ONLY)),--failed-only)

SERVER_TEST_ARGS = \
	$(if $(strip $(PYTEST_K)),--pytest-k "$(PYTEST_K)")

status:
	@$(NODE) tools/gameempire_dev.js status $(DEV_ARGS)

check:
	@$(NODE) tools/gameempire_dev.js check $(DEV_ARGS)

test-server:
	@$(NODE) tools/gameempire_dev.js test-server $(DEV_ARGS) $(SERVER_TEST_ARGS)

test-tools:
	@$(NODE) tools/gameempire_dev.js test-tools $(DEV_ARGS)

test-tools-full:
	@$(NODE) tools/gameempire_dev.js test-tools-full $(DEV_ARGS)

test-unity:
	@$(NODE) tools/gameempire_dev.js test-unity $(DEV_ARGS) $(UNITY_TEST_ARGS)

preflight:
	@$(NODE) tools/gameempire_dev.js preflight $(DEV_ARGS)

private-source:
	@$(NODE) tools/gameempire_dev.js set-private-source $(DEV_ARGS) $(if $(strip $(TAG)),"$(TAG)")

merge:
	@$(NODE) tools/gameempire_merge_latest.js $(MERGE_ARGS)

release:
	@$(NODE) tools/gameempire_merge_latest.js --release-only $(COMMON_RELEASE_ARGS)

help:
	@echo "Unity MCP fork tools"
	@echo ""
	@echo "Targets:"
	@echo "  make status             Show private fork versions and server package source"
	@echo "  make check              Validate private fork versions and server package source"
	@echo "  make test-server        Run Server pytest suite (PYTEST_K=camera optional)"
	@echo "  make test-tools         Run fast Node release/dev tooling tests"
	@echo "  make test-tools-full    Run Node tooling tests and Python characterization tests"
	@echo "  make test-unity         Run Unity EditMode tests through Server CLI (MODE=PlayMode optional)"
	@echo "  make preflight          Run check, tool tests, and Server pytest before private release"
	@echo "  make private-source     Update MCPForUnity/package.json private source (TAG=gameempire-mcp-vYYYYMMDD.N)"
	@echo "  make merge              Fetch upstream beta, merge into gameempire/protect, create immutable tag, update latest tag"
	@echo "  make release            Release current gameempire/protect without merging upstream"
	@echo ""
	@echo "Options:"
	@echo "  PYTEST_K=expr           Pytest -k expression for make test-server"
	@echo "  MODE=EditMode|PlayMode  Unity test mode for make test-unity"
	@echo "  FAILED_ONLY=1           Show only failed Unity tests"
	@echo "  TAG=x                   Private source tag for make private-source"
	@echo "  PRIVATE_REPO=url        Override private fork URL"
	@echo "  DRY_RUN=1               Print git/release commands without changing the repo"
	@echo "  PUSH=1                  Push target branch, immutable tag, and gameempire-mcp-latest (default)"
	@echo "  NO_PUSH=1               Do not push after the local branch/tag update"
	@echo "  UPSTREAM_BRANCH=x       Override upstream branch (default: beta)"
	@echo "  TARGET_BRANCH=x         Override target branch (default: gameempire/protect)"
