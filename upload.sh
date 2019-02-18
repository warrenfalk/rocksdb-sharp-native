#!/bin/bash

export REVISION=$(cat ./build-rocksdb.sh | grep ROCKSDBVERSION | sed -n -e '/^ROCKSDBVERSION=/ s/ROCKSDBVERSION=\(.*\)/\1/p')
export VERSION=$(cat ./version)
export RDBVERSION=$(cat ./rocksdbversion)
echo "REVISION = ${REVISION}"

PATH=./bin:${PATH}

hash curl || { echo "curl is required, install curl"; exit 1; }
hash jq || {
	# If this is windows, the jq executable can be downloaded
	OSINFO=$(uname)
	if [[ $OSINFO == *"MSYS"* || $OSINFO == *"MINGW"* ]]; then
		echo "Windows detected, will attempt to download jq"
		mkdir -p bin
		curl --silent -L 'https://github.com/stedolan/jq/releases/download/jq-1.6/jq-win64.exe' -o bin/jq.exe
	fi
}
hash jq || { echo "jq is required, install jq"; exit 1; }

# These can be overridden in ~/.rocksdb-sharp-upload-info
# also use .netrc for github login credentials
GITHUB_LOGIN="warrenfalk"

if [ -f ~/.rocksdb-sharp-upload-info ]; then
	. ~/.rocksdb-sharp-upload-info
fi

cd $(dirname $0)

upload() {
	RELEASE="$1"
	FILE="$2"
	
	RELEASE_URL=`curl https://api.github.com/repos/warrenfalk/rocksdb-sharp-native/releases --netrc-file ~/.netrc | jq ".[]|{name: .name, url: .url}|select(.name == \"${RELEASE}\")|.url" --raw-output`
	echo "Release URL: ${RELEASE_URL}"
	if [ "${RELEASE_URL}" == "" ]; then
	echo "Creating Release..."
		PAYLOAD="{\"tag_name\": \"${RELEASE}\", \"target_commitish\": \"master\", \"name\": \"${RELEASE}\", \"body\": \"RocksDb native ${RELEASE} (rocksdb ${RDBVERSION})\", \"draft\": true, \"prelease\": false }"
		echo "Sending:"
		echo ${PAYLOAD}
		echo "-----------"
		export RELEASE_INFO=$(curl --silent -H "Content-Type: application/json" -X POST -d "${PAYLOAD}" --netrc-file ~/.netrc ${CURLOPTIONS} https://api.github.com/repos/warrenfalk/rocksdb-sharp-native/releases)
	else
		export RELEASE_INFO=`curl --silent -H "Content-Type: application/json" --netrc-file ~/.netrc ${CURLOPTIONS} ${RELEASE_URL}`
		#echo "Release response for ${RELEASE_URL}"
		#echo ${RELEASE_INFO}
	fi

	echo "Response:"
	echo ${RELEASE_INFO}
	echo "-----------"
	UPLOADURL="$(echo "${RELEASE_INFO}" | jq .upload_url --raw-output)"
	echo "Upload URL:"
	echo "${UPLOADURL}"
	echo "-----------"
	if [ "$UPLOADURL" == "null" ]; then
		echo "Release creation not successful or unable to determine upload url:"
		echo "${DRAFTINFO}"
		echo "-----------"
		exit 1;
	fi
	UPLOADURLBASE="${UPLOADURL%\{*\}}"
	echo "Uploading Zip..."
	echo "to $UPLOADURLBASE"
	curl --progress-bar -H "Content-Type: application/zip" -X POST --data-binary @${FILE} --netrc-file ~/.netrc ${CURLOPTIONS} ${UPLOADURLBASE}?name=${FILE}
}

if [ -f ./rocksdb-${REVISION}/osx-x64/librocksdb.dylib ]; then
	echo "Uploading MAC native"
	ZIPFILE=rocksdb-${REVISION}-osx-x64.zip
	(cd ./rocksdb-${REVISION} && zip -r ../${ZIPFILE} ./)
	upload ${REVISION} ${ZIPFILE}
fi

if [ -f ./rocksdb-${REVISION}/win-x64/rocksdb.dll ]; then
	echo "Uploading Windows native"
	ZIPFILE=rocksdb-${REVISION}.win-x64.zip
	(cd ./rocksdb-${REVISION} && /c/Program\ Files/7-Zip/7z.exe a -r '..\'${ZIPFILE} .)
	upload ${REVISION} ${ZIPFILE}
fi

if [ -f ./rocksdb-${REVISION}/linux-x64/librocksdb.so ]; then
	echo "Uploading Linux native"
	ZIPFILE=rocksdb-${REVISION}-linux-x64.zip
	(cd ./rocksdb-${REVISION} && zip -r ../${ZIPFILE} ./)
	upload ${REVISION} ${ZIPFILE}
fi



