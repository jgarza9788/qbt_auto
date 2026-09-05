"""``python -m qbitflow`` / the ``qbitflow`` console script."""

from __future__ import annotations

import uvicorn

from qbitflow.config import get_config


def main() -> None:
    config = get_config()
    uvicorn.run(
        "qbitflow.main:app",
        host=config.host,
        port=config.port,
        log_config=None,
        access_log=False,
        proxy_headers=config.behind_proxy,
        forwarded_allow_ips="*" if config.behind_proxy else None,
    )


if __name__ == "__main__":
    main()
